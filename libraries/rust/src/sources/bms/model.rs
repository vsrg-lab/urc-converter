//! Source model of a BMS-family chart.

use std::collections::BTreeMap;

/// Source model of a BMS-family chart.
#[derive(Debug, Clone)]
pub struct BmsChart {
    pub pms: bool,
    pub base: u32,
    pub title: Option<String>,
    pub artist: Option<String>,
    pub play_level: Option<String>,
    pub bpm: Option<f64>,
    pub lntype: u32,
    pub lnobj: Option<String>,
    pub bpm_defs: BTreeMap<String, f64>,
    pub stop_defs: BTreeMap<String, f64>,
    pub scroll_defs: BTreeMap<String, f64>,
    pub rates: BTreeMap<i64, f64>,
    pub measures: BTreeMap<i64, BTreeMap<String, Vec<String>>>,
}
