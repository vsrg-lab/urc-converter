//! Source model of an osu!mania beatmap.

/// One `[TimingPoints]` entry, reduced to the fields we map.
#[derive(Debug, Clone)]
pub struct OsuTimingPoint {
    pub time: i64,
    pub beat_length: f64,
    pub meter: u64,
    pub uninherited: bool,
}

/// One `[HitObjects]` entry, reduced to the fields we map.
#[derive(Debug, Clone)]
pub struct OsuHitObject {
    pub x: i64,
    pub time: i64,
    pub is_hold: bool,
    pub end_time: Option<i64>,
}

/// Source model of an osu!mania beatmap.
#[derive(Debug, Clone, Default)]
pub struct OsuBeatmap {
    pub mode: i64,
    pub title: Option<String>,
    pub title_unicode: Option<String>,
    pub artist: Option<String>,
    pub artist_unicode: Option<String>,
    pub creator: Option<String>,
    pub version: Option<String>,
    pub circle_size: Option<f64>,
    pub overall_difficulty: Option<f64>,
    pub timing_points: Vec<OsuTimingPoint>,
    pub hit_objects: Vec<OsuHitObject>,
}
