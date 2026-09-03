//! Shared merge and validation helpers for source format converters.

use std::collections::{BTreeMap, HashSet};

use crate::error::{Result, UrcError};
use crate::model::{Meter, Note, NoteType, TimingPoint};

/// Rounds to integer milliseconds, half away from zero.
pub(crate) fn round_ms(value: f64) -> i64 {
    if value >= 0.0 {
        (value + 0.5) as i64
    } else {
        (value - 0.5) as i64
    }
}

/// First measure boundary at or after `time_ms`, assuming each BPM point
/// anchors a measure grid. Overshooting the next point snaps to that point
/// (the grid re-anchors there).
pub(crate) fn first_downbeat_after(bpm_points: &[(i64, f64, u64, u64)], time_ms: i64) -> Option<i64> {
    let mut points = bpm_points.to_vec();
    points.sort_by_key(|point| point.0);

    for (index, &(time, bpm, beats, _note_value)) in points.iter().enumerate() {
        let next_time = points.get(index + 1).map_or(i64::MAX, |point| point.0);
        if time_ms <= time {
            return Some(round_ms(time as f64));
        }
        if time_ms < next_time {
            let measure_ms = beats as f64 * 60000.0 / bpm;
            if measure_ms <= 0.0 {
                continue;
            }
            let k = ((time_ms - time) as f64 / measure_ms - 1e-9).ceil();
            let anchor = time as f64 + k * measure_ms;
            return Some(round_ms(anchor.min(next_time as f64)));
        }
    }
    None
}

/// Merges BPM and SV points into shifted `@Timing` entries.
///
/// `bpm_points` holds `(time_ms, bpm, beats, note_value)` — the meter — and
/// `sv_points` holds `(time_ms, multiplier)`, both in file order. Entries are
/// emitted only when the effective `(bpm, multiplier, meter)` state changes,
/// the timeline is shifted so the first note lands at 0 ms, and entries
/// falling negative are clamped to 0 (entries sharing a timestamp keep the
/// last state). A point is forced at `measure_anchor_ms` even without a state
/// change so the measure grid survives the clamp.
pub(crate) fn build_timing(
    bpm_points: &[(i64, f64, u64, u64)],
    sv_points: &[(i64, f64)],
    first_note_time: i64,
    source: &str,
    measure_anchor_ms: Option<i64>,
) -> Result<Vec<TimingPoint>> {
    let mut events: Vec<(i64, usize, bool, f64, u64, u64)> = bpm_points
        .iter()
        .enumerate()
        .map(|(index, (time, bpm, beats, note_value))| {
            (*time, index, true, *bpm, *beats, *note_value)
        })
        .chain(
            sv_points
                .iter()
                .enumerate()
                .map(|(index, (time, multiplier))| (*time, index, false, *multiplier, 0, 0)),
        )
        .collect();
    events.sort_by_key(|event| (event.0, event.1));

    let mut current_bpm: Option<f64> = None;
    let mut current_meter = (4_u64, 4_u64);
    let mut current_multiplier = 1.0_f64;
    let mut last: Option<(f64, f64, (u64, u64))> = None;
    let mut emitted: Vec<(i64, f64, f64, u64, u64)> = Vec::new();
    let mut i = 0;
    while i < events.len() {
        let time = events[i].0;
        while i < events.len() && events[i].0 == time {
            let (_, _, is_bpm, value, beats, note_value) = events[i];
            if is_bpm {
                current_bpm = Some(value);
                current_meter = (beats, note_value);
            } else {
                current_multiplier = value;
            }
            i += 1;
        }
        let bpm = match current_bpm {
            Some(bpm) => bpm,
            None => continue,
        };
        if last == Some((bpm, current_multiplier, current_meter)) {
            continue;
        }
        emitted.push((
            round_ms(time as f64),
            bpm,
            current_multiplier,
            current_meter.0,
            current_meter.1,
        ));
        last = Some((bpm, current_multiplier, current_meter));
    }

    if emitted.is_empty() {
        return Err(UrcError::new(
            "syntax",
            1,
            format!("{source}: no BPM timing point"),
        ));
    }

    if let Some(anchor) = measure_anchor_ms.map(|value| round_ms(value as f64))
        && !emitted.iter().any(|entry| entry.0 == anchor)
    {
        let mut active = emitted[0];
        for entry in &emitted {
            if entry.0 < anchor {
                active = *entry;
            }
        }
        emitted.push((anchor, active.1, active.2, active.3, active.4));
        emitted.sort_by_key(|entry| entry.0);
    }

    let mut shifted: BTreeMap<i64, (f64, f64, u64, u64)> = BTreeMap::new();
    for (time, bpm, multiplier, beats, note_value) in emitted {
        shifted.insert(
            (time - first_note_time).max(0),
            (bpm, multiplier, beats, note_value),
        );
    }

    let mut points: Vec<TimingPoint> = shifted
        .into_iter()
        .map(|(time, (bpm, multiplier, beats, note_value))| TimingPoint {
            timestamp_ms: time,
            bpm,
            meter: Meter {
                beats,
                note_value,
            },
            multiplier: (multiplier != 1.0).then_some(multiplier),
        })
        .collect();

    if let Some(first) = points.first()
        && first.timestamp_ms != 0
    {
        points.insert(
            0,
            TimingPoint {
                timestamp_ms: 0,
                bpm: first.bpm,
                meter: first.meter,
                multiplier: None,
            },
        );
    }

    Ok(points)
}

/// Rejects holds that overlap on the same lane (URC rule 21).
pub(crate) fn check_hold_overlap(notes: &[Note]) -> Result<()> {
    let mut open_lanes: HashSet<u32> = HashSet::new();

    let mut ordered: Vec<&Note> = notes.iter().collect();
    ordered.sort_by_key(|note| (note.timestamp_ms, note.lane));

    for note in ordered {
        if matches!(note.note_type, NoteType::Ls) {
            if !open_lanes.insert(note.lane) {
                return Err(UrcError::new(
                    "syntax",
                    1,
                    format!("overlapping holds on lane {}", note.lane),
                ));
            }
        } else if matches!(note.note_type, NoteType::Le) {
            open_lanes.remove(&note.lane);
        }
    }

    Ok(())
}
