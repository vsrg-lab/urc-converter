//! Mutable scan state shared by the parser modules.

use crate::model::{TimingPoint, Version};

/// A @Notes line before validation.
pub(super) struct RawNote {
    pub(super) timestamp: i64,
    pub(super) lane: i64,
    pub(super) type_token: String,
    pub(super) line: u32,
}

pub(super) struct ParseState {
    pub(super) seen: [bool; 6],
    pub(super) last_index: usize,
    pub(super) version: Version,
    pub(super) metadata: Vec<(String, String)>,
    pub(super) windows: Option<Vec<f64>>,
    pub(super) rates: Option<Vec<f64>>,
    pub(super) layout_type: Option<(u64, u64)>,
    pub(super) special: Option<Vec<i64>>,
    pub(super) special_seen: bool,
    pub(super) timing: Vec<TimingPoint>,
    pub(super) notes: Vec<RawNote>,
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
