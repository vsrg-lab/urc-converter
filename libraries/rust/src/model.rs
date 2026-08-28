//! URC data model shared by the parser, writer, and converters.

/// Type of a chart object in the @Notes section.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NoteType {
    /// Standard tap note.
    N,
    /// Long note start.
    Ls,
    /// Long note end.
    Le,
    /// Mine note.
    M,
    /// Fake note.
    F,
}

impl NoteType {
    pub fn token(self) -> &'static str {
        match self {
            NoteType::N => "N",
            NoteType::Ls => "LS",
            NoteType::Le => "LE",
            NoteType::M => "M",
            NoteType::F => "F",
        }
    }
}

/// File format version from the @URC header.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Version {
    pub major: u32,
    pub minor: u32,
}

/// Song and chart metadata from the @Metadata section.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Metadata {
    pub original: String,
    pub title: String,
    pub artist: String,
    pub creator: String,
    pub version: String,
}

/// Timing windows (ms) and scoring rates from the optional @Judgment section.
#[derive(Debug, Clone, PartialEq)]
pub struct Judgment {
    pub windows: Vec<f64>,
    pub rates: Vec<f64>,
}

/// Key layout from the @Layout section.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Layout {
    pub keys: u64,
    pub special_keys: u64,
    pub special_lanes: Option<Vec<u32>>,
}

impl Layout {
    pub fn total_lanes(&self) -> u64 {
        self.keys + self.special_keys
    }
}

/// Time signature numerator and denominator.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Meter {
    pub beats: u64,
    pub note_value: u64,
}

/// One @Timing entry. `multiplier` is `None` when omitted (= 1.0).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TimingPoint {
    pub timestamp_ms: i64,
    pub bpm: f64,
    pub meter: Meter,
    pub multiplier: Option<f64>,
}

/// One @Notes entry.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Note {
    pub timestamp_ms: i64,
    pub lane: u32,
    pub note_type: NoteType,
}

/// Complete parsed URC document.
#[derive(Debug, Clone, PartialEq)]
pub struct Chart {
    pub format_version: Version,
    pub metadata: Metadata,
    pub judgment: Option<Judgment>,
    pub layout: Layout,
    pub timing: Vec<TimingPoint>,
    pub notes: Vec<Note>,
}
