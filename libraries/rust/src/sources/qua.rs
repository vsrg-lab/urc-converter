//! Quaver (`.qua`) source parser and converter.

use yaml_rust2::{Yaml, YamlLoader};

use super::shared::{build_timing, check_hold_overlap, round_ms};
use crate::error::{Result, UrcError};
use crate::model::{Chart, Layout, Metadata, Note, NoteType, Version};

/// `(mode, key count)` pairs; the numbering is Quaver's own and not regular.
const MODE_KEYS: [(i64, u64); 10] = [
    (1, 4),
    (2, 7),
    (3, 1),
    (4, 2),
    (5, 3),
    (6, 5),
    (7, 6),
    (8, 8),
    (9, 9),
    (10, 10),
];

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

/// Parses `.qua` YAML text into a source model.
pub fn parse_qua(text: &str) -> Result<QuaMap> {
    let docs = YamlLoader::load_from_str(text).map_err(|error| {
        UrcError::new(
            "syntax",
            scan_line(&error),
            format!("invalid YAML: {error}"),
        )
    })?;
    let doc = docs.into_iter().next().unwrap_or(Yaml::Null);
    if doc.as_hash().is_none() {
        return Err(UrcError::new("syntax", 1, ".qua must be a YAML mapping"));
    }

    let mut qua = QuaMap {
        mode: as_i64(&doc["Mode"]).unwrap_or(1),
        has_scratch_key: doc["HasScratchKey"].as_bool().unwrap_or(false),
        title: doc["Title"].as_str().map(str::to_owned),
        artist: doc["Artist"].as_str().map(str::to_owned),
        creator: doc["Creator"].as_str().map(str::to_owned),
        difficulty_name: doc["DifficultyName"].as_str().map(str::to_owned),
        ..QuaMap::default()
    };

    for entry in entries(&doc, "TimingPoints")? {
        let signature = match as_i64(&entry["Signature"]) {
            None | Some(0) => 4, // legacy unset value, restored to 4/4 by the game
            Some(value) => value,
        };
        if signature != 3 && signature != 4 {
            return Err(UrcError::new(
                "syntax",
                1,
                format!("unsupported time signature: {signature}"),
            ));
        }

        qua.timing_points.push(QuaTimingPoint {
            start_time: round_ms(entry_f64(&entry["StartTime"], "StartTime")?),
            bpm: entry_f64(&entry["Bpm"], "Bpm")?,
            signature: signature as u64,
        });
    }

    for entry in entries(&doc, "ScrollSpeedFactors")? {
        qua.sv_points.push(QuaSvPoint {
            start_time: round_ms(entry_f64(&entry["StartTime"], "StartTime")?),
            multiplier: entry_f64(&entry["Multiplier"], "Multiplier")?,
        });
    }

    for entry in entries(&doc, "HitObjects")? {
        qua.hit_objects.push(QuaHitObject {
            start_time: entry_i64(&entry["StartTime"], "StartTime")?,
            lane: entry_i64(&entry["Lane"], "Lane")?,
            end_time: as_i64(&entry["EndTime"]).unwrap_or(0),
            mine: as_i64(&entry["Type"]).unwrap_or(0) == 1,
        });
    }

    Ok(qua)
}

/// Maps a Quaver chart onto a URC chart.
pub fn convert_qua(qua: &QuaMap) -> Result<Chart> {
    let keys = MODE_KEYS
        .iter()
        .find(|(mode, _)| *mode == qua.mode)
        .map(|(_, keys)| *keys)
        .ok_or_else(|| {
            UrcError::new(
                "unsupported-version",
                1,
                format!("unsupported Quaver mode: {}", qua.mode),
            )
        })?;

    let special_keys = u64::from(qua.has_scratch_key);
    let first_note_time = qua
        .hit_objects
        .iter()
        .map(|obj| obj.start_time)
        .min()
        .unwrap_or(0);

    let bpm_points: Vec<(i64, f64, u64)> = qua
        .timing_points
        .iter()
        .map(|point| (point.start_time, point.bpm, point.signature))
        .collect();
    let sv_points: Vec<(i64, f64)> = qua
        .sv_points
        .iter()
        .map(|point| (point.start_time, point.multiplier))
        .collect();

    let timing = build_timing(&bpm_points, &sv_points, first_note_time, ".qua")?;

    let total = keys + special_keys;
    let mut notes: Vec<Note> = Vec::new();

    for obj in &qua.hit_objects {
        let lane = obj.lane - 1;
        if !(0..total as i64).contains(&lane) {
            return Err(UrcError::new(
                "syntax",
                1,
                format!("lane out of range: {}", obj.lane),
            ));
        }

        if obj.end_time != 0 && obj.end_time < obj.start_time {
            return Err(UrcError::new(
                "syntax",
                1,
                format!(
                    "hold ends before it starts: {} < {}",
                    obj.end_time, obj.start_time
                ),
            ));
        }

        let lane = lane as u32;
        if obj.end_time != 0 {
            notes.push(Note {
                timestamp_ms: obj.start_time - first_note_time,
                lane,
                note_type: NoteType::Ls,
            });
            notes.push(Note {
                timestamp_ms: obj.end_time - first_note_time,
                lane,
                note_type: NoteType::Le,
            });
        } else if obj.mine {
            notes.push(Note {
                timestamp_ms: obj.start_time - first_note_time,
                lane,
                note_type: NoteType::M,
            });
        } else {
            notes.push(Note {
                timestamp_ms: obj.start_time - first_note_time,
                lane,
                note_type: NoteType::N,
            });
        }
    }

    check_hold_overlap(&notes)?;

    let missing: Vec<&str> = [
        ("Title", qua.title.as_deref()),
        ("Artist", qua.artist.as_deref()),
        ("Creator", qua.creator.as_deref()),
        ("DifficultyName", qua.difficulty_name.as_deref()),
    ]
    .into_iter()
    .filter_map(|(label, value)| value.is_none().then_some(label))
    .collect();

    if !missing.is_empty() {
        return Err(UrcError::new(
            "syntax",
            1,
            format!("missing metadata: {}", missing.join(", ")),
        ));
    }

    Ok(Chart {
        format_version: Version { major: 1, minor: 1 },
        metadata: Metadata {
            original: "Quaver".to_owned(),
            title: qua.title.clone().unwrap_or_default(),
            artist: qua.artist.clone().unwrap_or_default(),
            creator: qua.creator.clone().unwrap_or_default(),
            version: qua.difficulty_name.clone().unwrap_or_default(),
        },
        judgment: None,
        layout: Layout {
            keys,
            special_keys,
            special_lanes: qua.has_scratch_key.then(|| vec![keys as u32]),
        },
        timing,
        notes,
    })
}

fn scan_line(error: &yaml_rust2::scanner::ScanError) -> u32 {
    error.marker().line() as u32 + 1
}

/// Missing keys index to `Yaml::Null`, so absent entries fall through here.
fn entries<'a>(doc: &'a Yaml, key: &str) -> Result<Vec<&'a Yaml>> {
    match &doc[key] {
        Yaml::Array(items) => {
            if items.iter().any(|item| item.as_hash().is_none()) {
                return Err(UrcError::new(
                    "syntax",
                    1,
                    format!("{key} must be a list of mappings"),
                ));
            }
            Ok(items.iter().collect())
        }
        Yaml::Null | Yaml::BadValue => Ok(Vec::new()),
        _ => Err(UrcError::new(
            "syntax",
            1,
            format!("{key} must be a list of mappings"),
        )),
    }
}

fn as_f64(value: &Yaml) -> Option<f64> {
    match value {
        Yaml::Integer(int) => Some(*int as f64),
        Yaml::Real(text) => text.parse::<f64>().ok(),
        _ => None,
    }
}

fn as_i64(value: &Yaml) -> Option<i64> {
    match value {
        Yaml::Integer(int) => Some(*int),
        Yaml::Real(text) => text.parse::<f64>().ok().map(|value| value.round() as i64),
        _ => None,
    }
}

fn entry_f64(value: &Yaml, key: &str) -> Result<f64> {
    as_f64(value).ok_or_else(|| UrcError::new("syntax", 1, format!("missing {key}")))
}

fn entry_i64(value: &Yaml, key: &str) -> Result<i64> {
    as_i64(value).ok_or_else(|| UrcError::new("syntax", 1, format!("missing {key}")))
}
