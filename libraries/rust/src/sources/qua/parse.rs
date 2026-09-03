//! Parser for `.qua` YAML text.

use yaml_rust2::{Yaml, YamlLoader};

use crate::error::{Result, UrcError};
use crate::sources::shared::round_ms;

use super::model::{QuaHitObject, QuaMap, QuaSvPoint, QuaTimingPoint};

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
