//! Mapper from the Quaver source model onto a URC chart.

use std::collections::{BTreeMap, BTreeSet};

use crate::error::{Result, UrcError};
use crate::model::{Chart, Layout, Metadata, Note, NoteType, Version};
use crate::sources::shared::{build_timing, check_hold_overlap, first_downbeat_after};
use crate::strings::ZERO_LENGTH_HOLD;

use super::model::{QuaHitObject, QuaMap, QuaTimingPoint};

/// `(mode, key count)` pairs; the numbering is Quaver's own and not regular.
const MODE_KEYS: [(i64, u64); 10] = [
    (1, 4),
    (2, 7),
    (3, 1),
    (4, 2),
    (5, 3),
    (6, 5),
    (7, 6),
    (8, 8),
    (9, 9),
    (10, 10),
];

/// Maps a Quaver chart onto a URC chart.
pub fn convert_qua(qua: &QuaMap) -> Result<Chart> {
    let keys = MODE_KEYS
        .iter()
        .find(|(mode, _)| *mode == qua.mode)
        .map(|(_, keys)| *keys)
        .ok_or_else(|| {
            UrcError::new(
                "unsupported-version",
                1,
                format!("unsupported Quaver mode: {}", qua.mode),
            )
        })?;

    let special_keys = u64::from(qua.has_scratch_key);
    let first_note_time = qua
        .hit_objects
        .iter()
        .map(|obj| obj.start_time)
        .min()
        .unwrap_or(0);

    let (bpm_points, sv_points, anchor_points) = resolve_timing(qua, first_note_time)?;
    let timing = build_timing(
        &bpm_points,
        &sv_points,
        first_note_time,
        ".qua",
        first_downbeat_after(&anchor_points, first_note_time),
    )?;

    let total = keys + special_keys;
    let mut notes: Vec<Note> = Vec::new();

    for obj in &qua.hit_objects {
        let lane = obj.lane - 1;
        if !(0..total as i64).contains(&lane) {
            return Err(UrcError::new(
                "syntax",
                1,
                format!("lane out of range: {}", obj.lane),
            ));
        }

        let lane = lane as u32;
        if obj.mine {
            notes.push(Note {
                timestamp_ms: obj.start_time - first_note_time,
                lane,
                note_type: NoteType::M,
            });
        } else if obj.end_time != 0 {
            if obj.end_time < obj.start_time {
                return Err(UrcError::new(
                    "syntax",
                    1,
                    format!(
                        "hold ends before it starts: {} < {}",
                        obj.end_time, obj.start_time
                    ),
                ));
            }
            if obj.end_time == obj.start_time {
                return Err(UrcError::new(
                    ZERO_LENGTH_HOLD,
                    1,
                    format!("zero-length hold on lane {lane} at {}ms", obj.start_time),
                ));
            }
            notes.push(Note {
                timestamp_ms: obj.start_time - first_note_time,
                lane,
                note_type: NoteType::Ls,
            });
            notes.push(Note {
                timestamp_ms: obj.end_time - first_note_time,
                lane,
                note_type: NoteType::Le,
            });
        } else {
            notes.push(Note {
                timestamp_ms: obj.start_time - first_note_time,
                lane,
                note_type: NoteType::N,
            });
        }
    }

    check_hold_overlap(&notes)?;

    let missing: Vec<&str> = [
        ("Title", qua.title.as_deref()),
        ("Artist", qua.artist.as_deref()),
        ("Creator", qua.creator.as_deref()),
        ("DifficultyName", qua.difficulty_name.as_deref()),
    ]
    .into_iter()
    .filter_map(|(label, value)| value.is_none_or(str::is_empty).then_some(label))
    .collect();

    if !missing.is_empty() {
        return Err(UrcError::new(
            "syntax",
            1,
            format!("missing metadata: {}", missing.join(", ")),
        ));
    }

    Ok(Chart {
        format_version: Version { major: 1, minor: 1 },
        metadata: Metadata {
            original: "Quaver".to_owned(),
            title: qua.title.clone().unwrap_or_default(),
            artist: qua.artist.clone().unwrap_or_default(),
            creator: qua.creator.clone().unwrap_or_default(),
            version: qua.difficulty_name.clone().unwrap_or_default(),
        },
        judgment: None,
        layout: Layout {
            keys,
            special_keys,
            special_lanes: qua.has_scratch_key.then(|| vec![keys as u32]),
        },
        timing,
        notes,
    })
}

fn common_bpm(nonzero_tps: &[&QuaTimingPoint], hit_objects: &[QuaHitObject]) -> f64 {
    if hit_objects.is_empty() {
        return nonzero_tps[0].bpm.abs();
    }

    let mut last_time = hit_objects
        .iter()
        .map(|obj| {
            if !obj.mine && obj.end_time > 0 {
                obj.end_time
            } else {
                obj.start_time
            }
        })
        .max()
        .unwrap_or(0);

    let mut durations: Vec<(f64, i64)> = Vec::new();
    for (index, point) in nonzero_tps.iter().enumerate().rev() {
        if point.start_time > last_time {
            continue;
        }
        let duration = last_time - if index == 0 { 0 } else { point.start_time };
        last_time = point.start_time;
        let bpm = point.bpm.abs();
        if let Some(entry) = durations.iter_mut().find(|(b, _)| *b == bpm) {
            entry.1 += duration;
        } else {
            durations.push((bpm, duration));
        }
    }

    if durations.is_empty() {
        return nonzero_tps[0].bpm.abs();
    }

    let mut best_bpm = nonzero_tps[0].bpm.abs();
    let mut best_duration = -1_i64;
    for (bpm, duration) in durations {
        if duration > best_duration {
            best_duration = duration;
            best_bpm = bpm;
        }
    }
    best_bpm
}

type ResolvedTiming = (
    Vec<(i64, f64, u64, u64)>,
    Vec<(i64, f64)>,
    Vec<(i64, f64, u64, u64)>,
);

fn resolve_timing(qua: &QuaMap, first_note_time: i64) -> Result<ResolvedTiming> {
    if qua.timing_points.is_empty() {
        return Err(UrcError::new("syntax", 1, ".qua: no BPM timing point"));
    }

    let mut tp_by_time: BTreeMap<i64, &QuaTimingPoint> = BTreeMap::new();
    for tp in &qua.timing_points {
        tp_by_time.insert(tp.start_time, tp);
    }
    let mut sv_by_time: BTreeMap<i64, f64> = BTreeMap::new();
    for sv in &qua.sv_points {
        sv_by_time.insert(sv.start_time, sv.multiplier);
    }

    let dedup_tps: Vec<&QuaTimingPoint> = tp_by_time.values().copied().collect();
    let nonzero_tps: Vec<&QuaTimingPoint> = dedup_tps
        .iter()
        .copied()
        .filter(|tp| tp.bpm != 0.0)
        .collect();
    if nonzero_tps.is_empty() {
        return Err(UrcError::new(
            "syntax",
            1,
            ".qua: no positive BPM timing point",
        ));
    }

    let fallback_bpm = nonzero_tps[0].bpm.abs();
    let base_bpm = if qua.bpm_does_not_affect_sv {
        common_bpm(&nonzero_tps, &qua.hit_objects)
    } else {
        0.0
    };

    let first_tp = dedup_tps[0];
    let mut eff_bpm = if first_tp.bpm != 0.0 {
        first_tp.bpm.abs()
    } else {
        fallback_bpm
    };
    let mut bpm_factor = if first_tp.bpm > 0.0 {
        1.0
    } else if first_tp.bpm < 0.0 {
        -1.0
    } else {
        0.0
    };
    let mut beats = first_tp.signature;
    let mut raw_sv = if qua.bpm_does_not_affect_sv {
        qua.initial_scroll_velocity
    } else {
        1.0
    };

    let all_times: BTreeSet<i64> = tp_by_time
        .keys()
        .chain(sv_by_time.keys())
        .copied()
        .collect();
    let mut bpm_points: Vec<(i64, f64, u64, u64)> = Vec::new();
    let mut sv_points: Vec<(i64, f64)> = Vec::new();

    if qua.bpm_does_not_affect_sv
        && let Some(&first_time) = all_times.first()
        && first_time > first_note_time
    {
        let mut init_mult = if eff_bpm == base_bpm {
            raw_sv
        } else {
            raw_sv * (base_bpm / eff_bpm)
        };
        if init_mult == 0.0 {
            init_mult = 0.0;
        }
        if init_mult != 1.0 {
            bpm_points.push((first_note_time, eff_bpm, beats, 4));
            sv_points.push((first_note_time, init_mult));
        }
    }

    for time in all_times {
        let tp = tp_by_time.get(&time).copied();
        if let Some(&mult) = sv_by_time.get(&time) {
            raw_sv = mult;
        } else if tp.is_some() && !qua.bpm_does_not_affect_sv {
            raw_sv = 1.0;
        }

        if let Some(tp) = tp {
            beats = tp.signature;
            if tp.bpm > 0.0 {
                eff_bpm = tp.bpm;
                bpm_factor = 1.0;
            } else if tp.bpm < 0.0 {
                eff_bpm = tp.bpm.abs();
                bpm_factor = -1.0;
            } else {
                bpm_factor = 0.0;
            }
        }

        let mut eff_mult = if qua.bpm_does_not_affect_sv {
            if eff_bpm == base_bpm {
                raw_sv
            } else {
                raw_sv * (base_bpm / eff_bpm)
            }
        } else {
            raw_sv * bpm_factor
        };
        if eff_mult == 0.0 {
            eff_mult = 0.0;
        }

        bpm_points.push((time, eff_bpm, beats, 4));
        sv_points.push((time, eff_mult));
    }

    let anchor_points: Vec<(i64, f64, u64, u64)> = nonzero_tps
        .iter()
        .map(|tp| (tp.start_time, tp.bpm.abs(), tp.signature, 4))
        .collect();
    Ok((bpm_points, sv_points, anchor_points))
}
