//! Mapper from the osu!mania source model onto a URC chart.

use std::cmp::{max, min};
use std::collections::BTreeMap;

use crate::error::{Result, UrcError};
use crate::model::{Chart, Judgment, Layout, Metadata, Note, NoteType, Version};
use crate::sources::shared::{build_timing, check_hold_overlap, first_downbeat_after};
use crate::strings::ZERO_LENGTH_HOLD;

use super::model::OsuBeatmap;

const JUDGMENT_RATES: [f64; 6] = [100.0, 100.0, 66.67, 33.33, 16.67, 0.0];
const KEY_MIN: i64 = 1;
const KEY_MAX: i64 = 18;

fn type_order(note_type: NoteType) -> usize {
    match note_type {
        NoteType::N => 0,
        NoteType::Ls => 1,
        NoteType::Le => 2,
        NoteType::M => 3,
        NoteType::F => 4,
    }
}

/// Maps an osu!mania beatmap onto a URC chart.
pub fn convert_osu(beatmap: &OsuBeatmap) -> Result<Chart> {
    if beatmap.mode != 3 {
        return Err(UrcError::new(
            "unsupported-version",
            1,
            format!("unsupported game mode: {}", beatmap.mode),
        ));
    }

    let circle_size = beatmap
        .circle_size
        .ok_or_else(|| UrcError::new("syntax", 1, "missing CircleSize"))?;
    if !circle_size.is_finite() || circle_size != circle_size.trunc() {
        return Err(UrcError::new(
            "syntax",
            1,
            format!("CircleSize must be an integer: {circle_size}"),
        ));
    }

    let keys_i64 = circle_size as i64;
    if !(KEY_MIN..=KEY_MAX).contains(&keys_i64) {
        return Err(UrcError::new(
            "syntax",
            1,
            format!("CircleSize out of range: {keys_i64}"),
        ));
    }
    let keys = keys_i64 as u64;

    let first_note_time = beatmap
        .hit_objects
        .iter()
        .map(|obj| obj.time)
        .min()
        .unwrap_or(0);

    let (bpm_points, sv_points, anchor_points) = resolve_timing(beatmap)?;
    let timing = build_timing(
        &bpm_points,
        &sv_points,
        first_note_time,
        ".osu",
        first_downbeat_after(&anchor_points, first_note_time),
    )?;

    let mut notes: Vec<Note> = Vec::new();
    for obj in &beatmap.hit_objects {
        let lane = min(max((obj.x * keys_i64).div_euclid(512), 0), keys_i64 - 1) as u32;

        if obj.is_hold {
            let end_time = obj.end_time.unwrap_or_default();
            if end_time < obj.time {
                return Err(UrcError::new(
                    "syntax",
                    1,
                    format!("hold ends before it starts: {end_time} < {}", obj.time),
                ));
            }
            if end_time == obj.time {
                return Err(UrcError::new(
                    ZERO_LENGTH_HOLD,
                    1,
                    format!("zero-length hold on lane {lane} at {}ms", obj.time),
                ));
            }
            notes.push(Note {
                timestamp_ms: obj.time - first_note_time,
                lane,
                note_type: NoteType::Ls,
            });
            notes.push(Note {
                timestamp_ms: end_time - first_note_time,
                lane,
                note_type: NoteType::Le,
            });
        } else {
            notes.push(Note {
                timestamp_ms: obj.time - first_note_time,
                lane,
                note_type: NoteType::N,
            });
        }
    }

    notes.sort_by(|a, b| {
        a.timestamp_ms
            .cmp(&b.timestamp_ms)
            .then_with(|| a.lane.cmp(&b.lane))
            .then_with(|| type_order(a.note_type).cmp(&type_order(b.note_type)))
    });
    check_hold_overlap(&notes)?;

    let judgment = beatmap.overall_difficulty.map(|od| {
        let mut windows: Vec<f64> = vec![16.5];
        for base in [64.0, 97.0, 127.0, 151.0, 188.0] {
            let prev = *windows.last().unwrap();
            windows.push(prev.max(base - 3.0 * od + 0.5));
        }
        Judgment {
            windows,
            rates: JUDGMENT_RATES.to_vec(),
        }
    });

    let title = beatmap
        .title_unicode
        .clone()
        .filter(|value| !value.is_empty())
        .or_else(|| beatmap.title.clone());
    let artist = beatmap
        .artist_unicode
        .clone()
        .filter(|value| !value.is_empty())
        .or_else(|| beatmap.artist.clone());
    let missing: Vec<&str> = [
        ("Title", title.as_deref()),
        ("Artist", artist.as_deref()),
        ("Creator", beatmap.creator.as_deref()),
        ("Version", beatmap.version.as_deref()),
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
            original: "osu!mania".to_owned(),
            title: title.unwrap_or_default(),
            artist: artist.unwrap_or_default(),
            creator: beatmap.creator.clone().unwrap_or_default(),
            version: beatmap.version.clone().unwrap_or_default(),
        },
        judgment,
        layout: Layout {
            keys,
            special_keys: 0,
            special_lanes: None,
        },
        timing,
        notes,
    })
}

type ResolvedTiming = (
    Vec<(i64, f64, u64, u64)>,
    Vec<(i64, f64)>,
    Vec<(i64, f64, u64, u64)>,
);

fn resolve_timing(beatmap: &OsuBeatmap) -> Result<ResolvedTiming> {
    let mut tp_by_time: BTreeMap<i64, (f64, u64)> = BTreeMap::new();
    let mut sv_by_time: BTreeMap<i64, f64> = BTreeMap::new();

    for point in &beatmap.timing_points {
        if !point.beat_length.is_finite() || point.beat_length == 0.0 {
            return Err(UrcError::new(
                "syntax",
                1,
                "timing point with zero beat length",
            ));
        }
        let is_sv = !point.uninherited || point.beat_length < 0.0;
        if is_sv {
            if point.beat_length > 0.0 {
                return Err(UrcError::new(
                    "syntax",
                    1,
                    "inherited timing point with positive beat length",
                ));
            }
            sv_by_time.insert(point.time, -100.0 / point.beat_length);
        } else {
            let meter = if point.meter > 0 { point.meter } else { 4 };
            tp_by_time.insert(point.time, (60000.0 / point.beat_length, meter));
        }
    }

    if tp_by_time.is_empty() {
        return Err(UrcError::new("syntax", 1, ".osu: no BPM timing point"));
    }

    let mut all_times: Vec<i64> = Vec::with_capacity(tp_by_time.len() + sv_by_time.len());
    all_times.extend(tp_by_time.keys().copied());
    all_times.extend(sv_by_time.keys().copied());
    all_times.sort_unstable();
    all_times.dedup();
    let mut bpm_points: Vec<(i64, f64, u64, u64)> = Vec::new();
    let mut sv_points: Vec<(i64, f64)> = Vec::new();
    let anchor_points: Vec<(i64, f64, u64, u64)> = tp_by_time
        .iter()
        .map(|(&time, &(bpm, meter))| (time, bpm, meter, 4))
        .collect();

    let mut current_bpm: Option<f64> = None;
    let mut current_meter = 4_u64;
    let mut current_sv = 1.0_f64;
    let mut last_bpm: Option<(f64, u64)> = None;
    let mut last_sv = 1.0_f64;

    for time in all_times {
        let tp = tp_by_time.get(&time).copied();
        if let Some(&sv) = sv_by_time.get(&time) {
            current_sv = sv;
        } else if tp.is_some() {
            current_sv = 1.0;
        }

        if let Some((bpm, meter)) = tp {
            current_bpm = Some(bpm);
            current_meter = meter;
        }

        if let Some(bpm) = current_bpm {
            let bpm_state = Some((bpm, current_meter));
            if bpm_state != last_bpm {
                bpm_points.push((time, bpm, current_meter, 4));
                last_bpm = bpm_state;
            }
            if current_sv != last_sv {
                sv_points.push((time, current_sv));
                last_sv = current_sv;
            }
        }
    }

    Ok((bpm_points, sv_points, anchor_points))
}
