//! Chart assembly: note validation (Phase C) and model construction.

use std::collections::BTreeMap;

use super::state::ParseState;
use crate::error::{Result, UrcError};
use crate::model::{Chart, Judgment, Layout, Metadata, Note, NoteType};
use crate::strings::{REQUIRED_SECTIONS, rule};

pub(super) fn build(state: ParseState, end_line: u32) -> Result<Chart> {
    for name in REQUIRED_SECTIONS {
        let index = crate::strings::section_index(name).unwrap();
        if !state.seen[index] {
            return Err(UrcError::new(
                rule(2),
                end_line,
                format!("missing required section: {name}"),
            ));
        }
    }
    let ParseState {
        version,
        metadata,
        windows,
        rates,
        layout_type,
        special,
        timing,
        mut notes,
        ..
    } = state;
    let (keys, special_keys) = layout_type.expect("Layout is required and finalized");
    let special_lanes = special.map(|lanes| {
        lanes
            .iter()
            .map(|&lane| u32::try_from(lane).expect("special lanes are validated"))
            .collect::<Vec<u32>>()
    });
    let layout = Layout {
        keys,
        special_keys,
        special_lanes,
    };
    let total = layout.total_lanes();

    notes.sort_by_key(|note| (note.timestamp, note.lane));
    let mut chart_notes = Vec::with_capacity(notes.len());
    let mut open_ls: BTreeMap<i64, u32> = BTreeMap::new();
    for raw in &notes {
        if raw.timestamp < 0 {
            return Err(UrcError::new(
                rule(22),
                raw.line,
                "note timestamps must be non-negative",
            ));
        }
        if raw.lane < 0 || raw.lane as u64 >= total {
            return Err(UrcError::new(
                rule(18),
                raw.line,
                format!("lane out of range: {}", raw.lane),
            ));
        }
        let note_type = match raw.type_token.as_str() {
            "N" => NoteType::N,
            "LS" => NoteType::Ls,
            "LE" => NoteType::Le,
            "M" => NoteType::M,
            "F" => NoteType::F,
            other => {
                return Err(UrcError::new(
                    rule(19),
                    raw.line,
                    format!("unknown note type: {other:?}"),
                ));
            }
        };
        match note_type {
            NoteType::Le => match open_ls.remove(&raw.lane) {
                None => {
                    return Err(UrcError::new(
                        rule(20),
                        raw.line,
                        format!("LE without an open LS on lane {}", raw.lane),
                    ));
                }
                Some(_) => {}
            },
            NoteType::Ls => {
                if open_ls.contains_key(&raw.lane) {
                    return Err(UrcError::new(
                        rule(21),
                        raw.line,
                        format!("overlapping long notes on lane {}", raw.lane),
                    ));
                }
                open_ls.insert(raw.lane, raw.line);
            }
            _ => {}
        }
        chart_notes.push(Note {
            timestamp_ms: raw.timestamp,
            lane: raw.lane as u32,
            note_type,
        });
    }
    if let Some((&lane, &line)) = open_ls.iter().next() {
        return Err(UrcError::new(
            rule(20),
            line,
            format!("unterminated LS on lane {lane}"),
        ));
    }

    let field = |name: &str| -> String {
        metadata
            .iter()
            .find(|(known, _)| known == name)
            .map(|(_, value)| value.clone())
            .unwrap_or_else(|| panic!("metadata field {name} is validated"))
    };
    let judgment = windows.map(|windows| Judgment {
        windows,
        rates: rates.expect("Judgment fields are paired"),
    });
    Ok(Chart {
        format_version: version,
        metadata: Metadata {
            original: field("Original"),
            title: field("Title"),
            artist: field("Artist"),
            creator: field("Creator"),
            version: field("Version"),
        },
        judgment,
        layout,
        timing,
        notes: chart_notes,
    })
}
