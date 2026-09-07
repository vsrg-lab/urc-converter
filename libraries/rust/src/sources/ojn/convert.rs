//! Mapper from the O2Jam source model onto URC charts.

use std::collections::HashMap;

use crate::error::{Result, UrcError};
use crate::model::{Chart, Layout, Metadata, Note, NoteType, Version};

use super::super::shared::{build_timing, check_hold_overlap, round_ms};
use super::model::{OjnDifficulty, OjnFile};

const MEASURE_MS: f64 = 240000.0;
const METER_TOLERANCE: f64 = 1e-6;
const VERSIONS: [&str; 3] = ["Easy", "Normal", "Hard"];
const SCALES: [f64; 10] = [
    1.0,
    10.0,
    100.0,
    1000.0,
    10000.0,
    100000.0,
    1000000.0,
    10000000.0,
    100000000.0,
    1000000000.0,
];

type BpmPoint = (i64, f64, u64, u64);
type UrcNote = (i64, u32, NoteType);

/// Converts every difficulty of an OJN file into URC charts.
pub fn convert_ojn(file: &OjnFile) -> Result<Vec<Chart>> {
    file.difficulties
        .iter()
        .map(|difficulty| convert_chart(file, difficulty))
        .collect()
}

fn convert_chart(file: &OjnFile, difficulty: &OjnDifficulty) -> Result<Chart> {
    let mut events = difficulty.events.clone();
    events.sort_by(|left, right| {
        left.measure
            .cmp(&right.measure)
            .then(left.position.partial_cmp(&right.position).unwrap())
    });

    let mut time = 0.0;
    let mut bpm = file.bpm;
    let mut fraction = 1.0;
    let mut pointer = 0.0;
    let mut measure = 0_i32;
    let mut meter = (4_u64, 4_u64);
    let mut meter_dirty = false;
    let mut bpm_points: Vec<BpmPoint> = vec![(0, canonical_bpm(bpm), 4, 4)];
    let mut anchors: Vec<i64> = vec![0];
    let mut notes: Vec<UrcNote> = Vec::new();
    let mut holds: HashMap<u32, usize> = HashMap::new();

    for event in &events {
        while event.measure > measure {
            if fraction - pointer < 0.0 {
                return Err(UrcError::new(
                    "syntax",
                    event.offset,
                    "measure fraction cuts before the current position",
                ));
            }
            time += (MEASURE_MS * (fraction - pointer)) / bpm;
            anchors.push(round_ms(time));
            if meter_dirty {
                bpm_points.push((round_ms(time), canonical_bpm(bpm), 4, 4));
                meter = (4, 4);
                meter_dirty = false;
            }
            measure += 1;
            fraction = 1.0;
            pointer = 0.0;
        }
        time += (MEASURE_MS * (event.position - pointer)) / bpm;
        pointer = event.position;

        if event.channel == 0 {
            fraction = event.value;
            if let Some(approx) = fraction_meter(event.value)
                && approx != meter
            {
                meter = approx;
                meter_dirty = true;
                bpm_points.push((round_ms(time), canonical_bpm(bpm), meter.0, meter.1));
            }
        } else if event.channel == 1 {
            bpm_points.push((round_ms(time), canonical_bpm(event.value), meter.0, meter.1));
            bpm = event.value;
        } else {
            add_note(
                &mut notes,
                &mut holds,
                round_ms(time),
                event.channel as u32 - 2,
                event.kind,
            );
        }
    }
    for &index in holds.values() {
        notes[index].2 = NoteType::N;
    }

    let first_note_time = notes
        .iter()
        .filter(|note| note.2 != NoteType::Le)
        .map(|note| note.0)
        .min()
        .unwrap_or(0);
    let timing_points = build_timing(
        &bpm_points,
        &[],
        first_note_time,
        ".ojn",
        anchors
            .iter()
            .copied()
            .find(|&time| time >= first_note_time),
    )?;

    let type_order = |kind: NoteType| match kind {
        NoteType::N => 0,
        NoteType::Ls => 1,
        NoteType::Le => 2,
        NoteType::M => 3,
        NoteType::F => 4,
    };
    notes.sort_by_key(|note| (note.0, note.1, type_order(note.2)));
    let final_notes: Vec<Note> = notes
        .into_iter()
        .map(|(time, lane, note_type)| Note {
            timestamp_ms: time - first_note_time,
            lane,
            note_type,
        })
        .collect();
    check_hold_overlap(&final_notes)?;

    Ok(Chart {
        format_version: Version { major: 1, minor: 1 },
        metadata: Metadata {
            original: "O2Jam".to_string(),
            title: non_empty(&file.title, "Unknown"),
            artist: non_empty(&file.artist, "Unknown"),
            creator: non_empty(&file.noter, "Unknown"),
            version: VERSIONS[difficulty.index].to_string(),
        },
        judgment: None,
        layout: Layout {
            keys: 7,
            special_keys: 0,
            special_lanes: None,
        },
        timing: timing_points,
        notes: final_notes,
    })
}

fn add_note(
    notes: &mut Vec<UrcNote>,
    holds: &mut HashMap<u32, usize>,
    ms: i64,
    lane: u32,
    kind: u8,
) {
    if kind == 3 {
        if let Some(index) = holds.remove(&lane) {
            if ms <= notes[index].0 {
                notes[index].2 = NoteType::N;
            } else {
                notes.push((ms, lane, NoteType::Le));
            }
        }
        return;
    }
    if holds.contains_key(&lane) {
        return;
    }
    if kind == 2 {
        holds.insert(lane, notes.len());
        notes.push((ms, lane, NoteType::Ls));
    } else {
        notes.push((ms, lane, NoteType::N));
    }
}

/// Shortest decimal that round-trips the f32 BPM, widened to f64. Candidates
/// are enumerated explicitly instead of relying on formatter-specific
/// tie-breaking, so every language agrees.
fn canonical_bpm(value: f64) -> f64 {
    for (precision, &scale) in SCALES.iter().enumerate() {
        let base = (value * scale).trunc() as i64;
        for candidate in [base, base + 1, base - 1] {
            let text = decimal(candidate, precision);
            if let Ok(parsed) = text.parse::<f64>()
                && parsed as f32 as f64 == value
            {
                return parsed;
            }
        }
    }
    value
}

fn decimal(candidate: i64, precision: usize) -> String {
    let sign = if candidate < 0 { "-" } else { "" };
    let digits = candidate.unsigned_abs().to_string();
    if precision == 0 {
        return format!("{sign}{digits}");
    }
    let padded = format!("{digits:0>width$}", width = precision + 1);
    let split = padded.len() - precision;
    let mut text = format!("{sign}{}.{}", &padded[..split], &padded[split..]);
    while text.ends_with('0') {
        text.pop();
    }
    if text.ends_with('.') {
        text.pop();
    }
    text
}

/// Meter (beats, note_value) matching a measure fraction, if clean.
fn fraction_meter(value: f64) -> Option<(u64, u64)> {
    for note_value in 1..=64_u64 {
        let beats = (value * note_value as f64).round();
        if beats < 1.0 {
            continue;
        }
        if (value - beats / note_value as f64).abs() <= METER_TOLERANCE {
            let divisor = gcd(beats as u64, note_value);
            let reduced = (beats as u64 / divisor, note_value / divisor);
            return if reduced == (1, 1) {
                None
            } else {
                Some(reduced)
            };
        }
    }
    None
}

fn gcd(mut a: u64, mut b: u64) -> u64 {
    while b != 0 {
        let rest = a % b;
        a = b;
        b = rest;
    }
    a
}

fn non_empty(value: &str, fallback: &str) -> String {
    if value.is_empty() {
        fallback.to_string()
    } else {
        value.to_string()
    }
}
