//! Parser for URC 1.x documents.

use std::collections::BTreeMap;

use crate::error::{Result, UrcError};
use crate::model::{
    Chart, Judgment, Layout, Metadata, Meter, Note, NoteType, TimingPoint, Version,
};

const SECTIONS: [(&str, usize); 6] = [
    ("@URC", 0),
    ("@Metadata", 1),
    ("@Judgment", 2),
    ("@Layout", 3),
    ("@Timing", 4),
    ("@Notes", 5),
];
const REQUIRED_SECTIONS: [&str; 4] = ["@Metadata", "@Layout", "@Timing", "@Notes"];
const METADATA_FIELDS: [&str; 5] = ["Original", "Title", "Artist", "Creator", "Version"];

/// Parses and validates URC text into a [`Chart`].
pub fn parse(text: &str) -> Result<Chart> {
    let text = text.strip_prefix('\u{feff}').unwrap_or(text);
    let lines: Vec<&str> = text.lines().collect();
    let mut state = ParseState::default();

    scan(&lines, &mut state)?;
    build(state, lines.len() as u32 + 1)
}

struct RawNote {
    timestamp: i64,
    lane: i64,
    type_token: String,
    line: u32,
}

struct ParseState {
    seen: [bool; 6],
    last_index: usize,
    version: Version,
    metadata: Vec<(String, String)>,
    windows: Option<Vec<f64>>,
    rates: Option<Vec<f64>>,
    layout_type: Option<(u64, u64)>,
    special: Option<Vec<i64>>,
    special_seen: bool,
    timing: Vec<TimingPoint>,
    notes: Vec<RawNote>,
}

impl Default for ParseState {
    fn default() -> Self {
        Self {
            seen: [true, false, false, false, false, false],
            last_index: 0,
            version: Version { major: 1, minor: 1 },
            metadata: Vec::new(),
            windows: None,
            rates: None,
            layout_type: None,
            special: None,
            special_seen: false,
            timing: Vec::new(),
            notes: Vec::new(),
        }
    }
}

fn scan(lines: &[&str], state: &mut ParseState) -> Result<()> {
    let mut current = "@URC";
    for (offset, raw) in lines.iter().enumerate() {
        let line_no = (offset + 1) as u32;
        let text = raw.trim();
    }
}
