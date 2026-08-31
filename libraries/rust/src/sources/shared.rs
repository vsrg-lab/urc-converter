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

/// Merges BPM and SV points into shifted `@Timing` entries.
///
/// `bpm_points` holds `(time_ms, bpm, beats)` and `sv_points` holds
/// `(time_ms, multiplier)`, both in file order. Entries are emitted only when
/// the effective `(bpm, multiplier)` pair changes, the timeline is shifted so
/// the first note lands at 0 ms, and entries falling negative are clamped to
/// 0 (entries sharing a timestamp keep the last state).
pub(crate) fn build_timing(
    bpm_points: &[(i64, f64, u64)],
    sv_points: &[(i64, f64)],
    first_note_time: i64,
    source: &str,
) -> Result<Vec<TimingPoint>> {
    let mut events: Vec<(i64, usize, bool, f64, u64)> = bpm_points
        .iter()
        .enumerate()
        .map(|(index, (time, bpm, beats))| (*time, index, true, *bpm, *beats))
        .chain(
            sv_points
                .iter()
                .enumerate()
                .map(|(index, (time, multiplier))| (*time, index, false, *multiplier, 0)),
        )
        .collect();
    events.sort_by_key(|event| (event.0, event.1));

    let mut current_bpm: Option<f64> = None;
    let mut current_beats = 4_u64;
    let mut current_multiplier = 1.0_f64;
    let mut last: Option<(f64, f64, u64)> = None;
    let mut emitted: Vec<(i64, f64, f64, u64)> = Vec::new();
    let mut i = 0;
    while i < events.len() {
        let time = events[i].0;
        while i < events.len() && events[i].0 == time {
            let (_, _, is_bpm, value, beats) = events[i];
            if is_bpm {
                current_bpm = Some(value);
                current_beats = beats;
            } else {
                current_multiplier = value;
            }
            i += 1;
        }
        let bpm = match current_bpm {
            Some(bpm) => bpm,
            None => continue,
        };
        if last == Some((bpm, current_multiplier, current_beats)) {
            continue;
        }
        emitted.push((
            round_ms(time as f64),
            bpm,
            current_multiplier,
            current_beats,
        ));
        last = Some((bpm, current_multiplier, current_beats));
    }

    if emitted.is_empty() {
        return Err(UrcError::new(
            "syntax",
            1,
            format!("{source}: no BPM timing point"),
        ));
    }

    let mut shifted: BTreeMap<i64, (f64, f64, u64)> = BTreeMap::new();
    for (time, bpm, multiplier, beats) in emitted {
        shifted.insert((time - first_note_time).max(0), (bpm, multiplier, beats));
    }

    let mut points: Vec<TimingPoint> = shifted
        .into_iter()
        .map(|(time, (bpm, multiplier, beats))| TimingPoint {
            timestamp_ms: time,
            bpm,
            meter: Meter {
                beats,
                note_value: 4,
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
