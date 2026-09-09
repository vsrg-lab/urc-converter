//! Note parsing and URC note assembly for StepMania simfiles.

use std::collections::HashMap;

use crate::error::{Result, UrcError};
use crate::model::NoteType;

use super::super::shared::round_ms;
use super::model::{NoteKind, SmNote, Timing};
use super::preprocess::rows;

const ROLL_TAP_SPACING_MS: i64 = 500;

pub(crate) type UrcNote = (i64, u32, NoteType);

pub(crate) fn parse_note_data(data: &str, lanes: u32) -> Result<Vec<SmNote>> {
    let mut notes: Vec<SmNote> = Vec::new();
    let mut open_holds: HashMap<u32, usize> = HashMap::new();

    let mut measure = 0_u64;
    for part in data.split(',') {
        if part.is_empty() {
            continue;
        }
        let content: Vec<&str> = part
            .split('\n')
            .map(|raw| raw.trim_matches([' ', '\t', '\r']))
            .filter(|line| !line.is_empty())
            .collect();
        let total = content.len() as f64;
        for (index, line) in content.iter().enumerate() {
            let row = rows((measure as f64 + index as f64 / total) * 4.0);
            let chars: Vec<char> = line.chars().collect();
            let mut track = 0_u32;
            let mut position = 0_usize;
            while track < lanes && position < chars.len() {
                let ch = chars[position];
                position += 1;
                match ch {
                    '1' => notes.push(SmNote {
                        row,
                        track,
                        kind: NoteKind::Tap,
                        tail_row: None,
                    }),
                    '2' | '4' => {
                        if open_holds.contains_key(&track) {
                            return Err(UrcError::new(
                                "syntax",
                                1,
                                format!("overlapping hold head at row {row}"),
                            ));
                        }
                        notes.push(SmNote {
                            row,
                            track,
                            kind: if ch == '2' { NoteKind::Hold } else { NoteKind::Roll },
                            tail_row: None,
                        });
                        open_holds.insert(track, notes.len() - 1);
                    }
                    '3' => {
                        let index = open_holds
                            .remove(&track)
                            .ok_or_else(|| UrcError::new("syntax", 1, format!("hold tail without a head at row {row}")))?;
                        notes[index].tail_row = Some(row);
                    }
                    'M' => notes.push(SmNote {
                        row,
                        track,
                        kind: NoteKind::Mine,
                        tail_row: None,
                    }),
                    'L' => notes.push(SmNote {
                        row,
                        track,
                        kind: NoteKind::Lift,
                        tail_row: None,
                    }),
                    'F' => notes.push(SmNote {
                        row,
                        track,
                        kind: NoteKind::Fake,
                        tail_row: None,
                    }),
                    _ => {}
                }
                if position < chars.len() && chars[position] == '[' {
                    match chars[position..].iter().position(|&c| c == ']') {
                        Some(end) => position += end + 1,
                        None => position = chars.len(),
                    }
                }
                track += 1;
            }
        }
        measure += 1;
    }

    if !open_holds.is_empty() {
        return Err(UrcError::new("syntax", 1, "hold note without a tail"));
    }
    Ok(notes)
}

pub(crate) fn build_urc_notes(
    timing: &Timing,
    notes: &[SmNote],
    head_times: &[f64],
    tail_times: &[f64],
) -> Result<Vec<UrcNote>> {
    let fake_ranges: Vec<(i64, i64)> = timing
        .fakes
        .iter()
        .map(|&(beat, length)| (rows(beat), rows(beat) + rows(length)))
        .collect();
    let mut urc_notes: Vec<UrcNote> = Vec::new();

    for (index, note) in notes.iter().enumerate() {
        let head_ms = round_ms(head_times[index] * 1000.0);
        if fake_ranges.iter().any(|&(start, end)| start <= note.row && note.row < end) {
            urc_notes.push((head_ms, note.track, NoteType::F));
            continue;
        }
        match note.kind {
            NoteKind::Hold => {
                let tail_ms = round_ms(tail_times[index] * 1000.0);
                if tail_ms <= head_ms {
                    return Err(UrcError::new(
                        "syntax",
                        1,
                        format!("hold on lane {} collapses to zero length", note.track),
                    ));
                }
                urc_notes.push((head_ms, note.track, NoteType::Ls));
                urc_notes.push((tail_ms, note.track, NoteType::Le));
            }
            NoteKind::Roll => {
                let end_ms = round_ms(tail_times[index] * 1000.0);
                urc_notes.push((head_ms, note.track, NoteType::N));
                let mut tap_ms = head_ms + ROLL_TAP_SPACING_MS;
                while tap_ms < end_ms {
                    urc_notes.push((tap_ms, note.track, NoteType::N));
                    tap_ms += ROLL_TAP_SPACING_MS;
                }
            }
            NoteKind::Mine => urc_notes.push((head_ms, note.track, NoteType::M)),
            NoteKind::Fake => urc_notes.push((head_ms, note.track, NoteType::F)),
            NoteKind::Tap | NoteKind::Lift => urc_notes.push((head_ms, note.track, NoteType::N)),
        }
    }
    Ok(urc_notes)
}

pub(crate) fn first_non_empty<'a>(first: &'a str, second: &'a str, fallback: &'a str) -> &'a str {
    if !first.is_empty() {
        first
    } else if !second.is_empty() {
        second
    } else {
        fallback
    }
}

pub(crate) fn difficulty_name(difficulty: &str, description: &str) -> &'static str {
    const NAMES: &[(&str, &str)] = &[
        ("beginner", "Beginner"),
        ("easy", "Easy"),
        ("basic", "Easy"),
        ("light", "Easy"),
        ("medium", "Medium"),
        ("another", "Medium"),
        ("trick", "Medium"),
        ("standard", "Medium"),
        ("difficult", "Medium"),
        ("hard", "Hard"),
        ("ssr", "Hard"),
        ("maniac", "Hard"),
        ("heavy", "Hard"),
        ("smaniac", "Challenge"),
        ("challenge", "Challenge"),
        ("expert", "Challenge"),
        ("oni", "Challenge"),
        ("edit", "Edit"),
    ];
    let key = difficulty.trim().to_lowercase();
    let mut name = NAMES
        .iter()
        .find(|entry| entry.0 == key)
        .map(|entry| entry.1)
        .unwrap_or("Edit");
    if name == "Hard" && matches!(description.trim().to_lowercase().as_str(), "smaniac" | "challenge") {
        name = "Challenge";
    }
    name
}
