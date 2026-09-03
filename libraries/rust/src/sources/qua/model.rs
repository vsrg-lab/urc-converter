//! Source model of a .qua chart.

/// One TimingPoints entry.
#[derive(Debug, Clone)]
pub struct QuaTimingPoint {
    pub start_time: i64,
    pub bpm: f64,
    pub signature: u64,
}

/// One ScrollSpeedFactors entry.
#[derive(Debug, Clone)]
pub struct QuaSvPoint {
    pub start_time: i64,
    pub multiplier: f64,
}

/// One HitObjects entry.
#[derive(Debug, Clone)]
pub struct QuaHitObject {
    pub start_time: i64,
    pub lane: i64,
    pub end_time: i64,
    pub mine: bool,
}

/// Source model of a .qua chart.
#[derive(Debug, Clone, Default)]
pub struct QuaMap {
    pub mode: i64,
    pub has_scratch_key: bool,
    pub title: Option<String>,
    pub artist: Option<String>,
    pub creator: Option<String>,
    pub difficulty_name: Option<String>,
    pub timing_points: Vec<QuaTimingPoint>,
    pub sv_points: Vec<QuaSvPoint>,
    pub hit_objects: Vec<QuaHitObject>,
}
