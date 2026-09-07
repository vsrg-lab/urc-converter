//! Source model of an O2Jam (`.ojn`) chart file.

/// One event of a note-section package, in file order.
#[derive(Debug, Clone, PartialEq)]
pub struct OjnEvent {
    pub measure: i32,
    /// Package grid position: raw event index over the package total.
    pub position: f64,
    pub channel: u16,
    /// f32 payload of channels 0 (measure fraction) and 1 (BPM change).
    pub value: f64,
    /// Note kind from `type % 4`: 0 normal, 2 hold, 3 release.
    pub kind: u8,
    /// Byte offset of the event, for error locations.
    pub offset: u32,
}

/// Events of one difficulty section.
#[derive(Debug, Clone, PartialEq)]
pub struct OjnDifficulty {
    pub index: usize,
    pub events: Vec<OjnEvent>,
}

/// Parsed OJN file; the header BPM drives the timing walk.
#[derive(Debug, Clone, PartialEq)]
pub struct OjnFile {
    pub title: String,
    pub artist: String,
    pub noter: String,
    pub bpm: f64,
    pub difficulties: Vec<OjnDifficulty>,
}
