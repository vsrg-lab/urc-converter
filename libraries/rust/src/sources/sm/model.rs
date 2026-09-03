//! Source model of a StepMania (`.sm`/`.ssc`) simfile.

/// Timing segments in effect at the song level or for one chart.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct Timing {
    pub offset: f64,
    pub bpms: Vec<(f64, f64)>,
    pub stops: Vec<(f64, f64)>,
    pub delays: Vec<(f64, f64)>,
    pub warps: Vec<(f64, f64)>,
    pub scrolls: Vec<(f64, f64)>,
    pub timesigs: Vec<(f64, u64, u64)>,
    pub fakes: Vec<(f64, f64)>,
}

/// One note head; hold/roll pairs carry the tail row.
#[derive(Debug, Clone, PartialEq)]
pub struct SmNote {
    pub row: i64,
    pub track: u32,
    pub kind: NoteKind,
    pub tail_row: Option<i64>,
}

/// Kind of a note head in the note data.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NoteKind {
    Tap,
    Hold,
    Roll,
    Mine,
    Lift,
    Fake,
}

/// One chart block (`#NOTES` in `.sm`, `#NOTEDATA` in `.ssc`).
#[derive(Debug, Clone, Default, PartialEq)]
pub struct SmChart {
    pub steps_type: String,
    pub description: String,
    pub difficulty: String,
    pub chartname: String,
    pub credit: String,
    /// `None`: inherit the song-level timing.
    pub timing: Option<Timing>,
    pub notes: Vec<SmNote>,
}

/// Parsed simfile: song metadata, song timing, and charts.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct SmFile {
    pub title: String,
    pub subtitle: String,
    pub artist: String,
    pub credit: String,
    pub timing: Timing,
    pub charts: Vec<SmChart>,
}
