//! Parser for `.qua` YAML text.

use std::collections::HashMap;

use yaml_rust2::parser::{Event, MarkedEventReceiver, Parser};
use yaml_rust2::scanner::{Marker, ScanError, TScalarStyle};

use crate::error::{Result, UrcError};
use crate::sources::shared::round_ms;

use super::model::{QuaHitObject, QuaMap, QuaSvPoint, QuaTimingPoint};

#[derive(Debug, Clone)]
enum RawYaml {
    Null,
    Scalar(String),
    Seq(Vec<RawYaml>),
    Map(Vec<(String, RawYaml)>),
}

enum Frame {
    Seq(Vec<RawYaml>, usize),
    Map {
        entries: Vec<(String, RawYaml)>,
        pending_key: Option<String>,
        anchor: usize,
    },
}

struct RawLoader {
    docs: Vec<RawYaml>,
    stack: Vec<Frame>,
    anchors: HashMap<usize, RawYaml>,
}

impl RawLoader {
    fn new() -> Self {
        Self {
            docs: Vec::new(),
            stack: Vec::new(),
            anchors: HashMap::new(),
        }
    }

    fn insert(&mut self, value: RawYaml, anchor: usize) {
        if anchor > 0 {
            self.anchors.insert(anchor, value.clone());
        }
        match self.stack.last_mut() {
            None => self.docs.push(value),
            Some(Frame::Seq(items, _)) => items.push(value),
            Some(Frame::Map {
                entries,
                pending_key,
                ..
            }) => {
                if let Some(key) = pending_key.take() {
                    entries.push((key, value));
                } else {
                    let key = match value {
                        RawYaml::Scalar(text) => text,
                        _ => String::new(),
                    };
                    *pending_key = Some(key);
                }
            }
        }
    }
}

impl MarkedEventReceiver for RawLoader {
    fn on_event(&mut self, ev: Event, _mark: Marker) {
        match ev {
            Event::Scalar(text, style, anchor, _) => {
                let is_null = style == TScalarStyle::Plain
                    && matches!(text.as_str(), "" | "~" | "null" | "Null" | "NULL");
                let node = if is_null {
                    RawYaml::Null
                } else {
                    RawYaml::Scalar(text)
                };
                self.insert(node, anchor);
            }
            Event::Alias(id) => {
                let value = self.anchors.get(&id).cloned().unwrap_or(RawYaml::Null);
                self.insert(value, 0);
            }
            Event::SequenceStart(anchor, _) => {
                self.stack.push(Frame::Seq(Vec::new(), anchor));
            }
            Event::SequenceEnd => {
                if let Some(Frame::Seq(items, anchor)) = self.stack.pop() {
                    self.insert(RawYaml::Seq(items), anchor);
                }
            }
            Event::MappingStart(anchor, _) => {
                self.stack.push(Frame::Map {
                    entries: Vec::new(),
                    pending_key: None,
                    anchor,
                });
            }
            Event::MappingEnd => {
                if let Some(Frame::Map {
                    entries, anchor, ..
                }) = self.stack.pop()
                {
                    self.insert(RawYaml::Map(entries), anchor);
                }
            }
            Event::Nothing
            | Event::StreamStart
            | Event::StreamEnd
            | Event::DocumentStart
            | Event::DocumentEnd => {}
        }
    }
}

/// Parses `.qua` YAML text into a source model.
pub fn parse_qua(text: &str) -> Result<QuaMap> {
    let cleaned = text.trim_start_matches('\u{feff}');
    let mut loader = RawLoader::new();
    Parser::new_from_str(cleaned)
        .load(&mut loader, false)
        .map_err(|error| {
            UrcError::new(
                "syntax",
                scan_line(&error),
                format!("invalid YAML: {error}"),
            )
        })?;
    let doc = loader.docs.into_iter().next().unwrap_or(RawYaml::Null);
    let RawYaml::Map(root) = doc else {
        return Err(UrcError::new("syntax", 1, ".qua must be a YAML mapping"));
    };

    let mut qua = QuaMap {
        mode: parse_mode(get_field(&root, "Mode"))?,
        has_scratch_key: parse_bool(get_field(&root, "HasScratchKey"), false),
        bpm_does_not_affect_sv: parse_bool(
            get_field(&root, "BPMDoesNotAffectScrollVelocity"),
            false,
        ),
        initial_scroll_velocity: parse_f64(
            get_field(&root, "InitialScrollVelocity"),
            "InitialScrollVelocity",
            0.0,
        )?,
        title: parse_string(get_field(&root, "Title"), "Title")?,
        artist: parse_string(get_field(&root, "Artist"), "Artist")?,
        creator: parse_string(get_field(&root, "Creator"), "Creator")?,
        difficulty_name: parse_string(get_field(&root, "DifficultyName"), "DifficultyName")?,
        ..QuaMap::default()
    };

    for entry in entries(&root, "TimingPoints")? {
        qua.timing_points.push(QuaTimingPoint {
            start_time: round_ms(parse_f64(get_field(entry, "StartTime"), "StartTime", 0.0)?),
            bpm: parse_f64(get_field(entry, "Bpm"), "Bpm", 0.0)?,
            signature: parse_signature(get_field(entry, "Signature"))?,
        });
    }

    for entry in entries(&root, "SliderVelocities")? {
        qua.sv_points.push(QuaSvPoint {
            start_time: round_ms(parse_f64(get_field(entry, "StartTime"), "StartTime", 0.0)?),
            multiplier: parse_f64(get_field(entry, "Multiplier"), "Multiplier", 0.0)?,
        });
    }

    for entry in entries(&root, "HitObjects")? {
        qua.hit_objects.push(QuaHitObject {
            start_time: round_ms(parse_f64(get_field(entry, "StartTime"), "StartTime", 0.0)?),
            lane: round_ms(parse_f64(get_field(entry, "Lane"), "Lane", 0.0)?),
            end_time: round_ms(parse_f64(get_field(entry, "EndTime"), "EndTime", 0.0)?),
            mine: parse_hit_object_type(get_field(entry, "Type"))? == 1,
        });
    }

    Ok(qua)
}

fn scan_line(error: &ScanError) -> u32 {
    error.marker().line() as u32 + 1
}

fn get_field<'a>(entries: &'a [(String, RawYaml)], key: &str) -> Option<&'a RawYaml> {
    entries
        .iter()
        .rev()
        .find(|(k, _)| k == key)
        .map(|(_, value)| value)
}

fn entries<'a>(root: &'a [(String, RawYaml)], key: &str) -> Result<Vec<&'a [(String, RawYaml)]>> {
    match get_field(root, key) {
        None | Some(RawYaml::Null) => Ok(Vec::new()),
        Some(RawYaml::Seq(items)) => items
            .iter()
            .map(|item| match item {
                RawYaml::Map(map) => Ok(map.as_slice()),
                _ => Err(UrcError::new(
                    "syntax",
                    1,
                    format!("{key} must be a list of mappings"),
                )),
            })
            .collect(),
        Some(_) => Err(UrcError::new(
            "syntax",
            1,
            format!("{key} must be a list of mappings"),
        )),
    }
}

fn parse_string(value: Option<&RawYaml>, key: &str) -> Result<Option<String>> {
    match value {
        None | Some(RawYaml::Null) => Ok(None),
        Some(RawYaml::Scalar(text)) => Ok(Some(text.clone())),
        Some(_) => Err(UrcError::new("syntax", 1, format!("invalid {key}"))),
    }
}

fn parse_bool(value: Option<&RawYaml>, default: bool) -> bool {
    match value {
        Some(RawYaml::Scalar(text)) => match text.trim().to_ascii_lowercase().as_str() {
            "true" => true,
            "false" => false,
            _ => default,
        },
        _ => default,
    }
}

fn parse_f64(value: Option<&RawYaml>, key: &str, default: f64) -> Result<f64> {
    let text = match value {
        None | Some(RawYaml::Null) => return Ok(default),
        Some(RawYaml::Scalar(text)) => text,
        Some(_) => return Err(UrcError::new("syntax", 1, format!("invalid {key}"))),
    };
    let parsed = text
        .trim()
        .parse::<f64>()
        .map_err(|_| UrcError::new("syntax", 1, format!("invalid {key}: {text:?}")))?;
    if !parsed.is_finite() {
        return Err(UrcError::new("syntax", 1, format!("invalid {key}: {parsed}")));
    }
    Ok(parsed)
}

fn parse_mode(value: Option<&RawYaml>) -> Result<i64> {
    let text = match value {
        None | Some(RawYaml::Null) => return Ok(1),
        Some(RawYaml::Scalar(text)) => text,
        Some(_) => return Err(UrcError::new("syntax", 1, "invalid Mode")),
    };
    let trimmed = text.trim();
    match trimmed.to_ascii_lowercase().as_str() {
        "keys4" => Ok(1),
        "keys7" => Ok(2),
        "keys1" => Ok(3),
        "keys2" => Ok(4),
        "keys3" => Ok(5),
        "keys5" => Ok(6),
        "keys6" => Ok(7),
        "keys8" => Ok(8),
        "keys9" => Ok(9),
        "keys10" => Ok(10),
        _ => trimmed.parse::<i64>().map_err(|_| {
            UrcError::new(
                "unsupported-version",
                1,
                format!("unsupported Quaver mode: {trimmed}"),
            )
        }),
    }
}

fn parse_signature(value: Option<&RawYaml>) -> Result<u64> {
    let text = match value {
        None | Some(RawYaml::Null) => return Ok(4),
        Some(RawYaml::Scalar(text)) => text,
        Some(_) => return Err(UrcError::new("syntax", 1, "invalid Signature")),
    };
    let trimmed = text.trim();
    match trimmed.to_ascii_lowercase().as_str() {
        "quadruple" => return Ok(4),
        "triple" => return Ok(3),
        _ => {}
    }
    let parsed = trimmed.parse::<i64>().map_err(|_| {
        UrcError::new(
            "syntax",
            1,
            format!("unsupported time signature: {trimmed}"),
        )
    })?;
    match parsed {
        0 => Ok(4),
        v if v >= 1 => Ok(v as u64),
        other => Err(UrcError::new(
            "syntax",
            1,
            format!("unsupported time signature: {other}"),
        )),
    }
}

fn parse_hit_object_type(value: Option<&RawYaml>) -> Result<i64> {
    let text = match value {
        None | Some(RawYaml::Null) => return Ok(0),
        Some(RawYaml::Scalar(text)) => text,
        Some(_) => return Err(UrcError::new("syntax", 1, "invalid Type")),
    };
    let trimmed = text.trim();
    match trimmed.to_ascii_lowercase().as_str() {
        "normal" => return Ok(0),
        "mine" => return Ok(1),
        _ => {}
    }
    let parsed = trimmed.parse::<i64>().map_err(|_| {
        UrcError::new(
            "syntax",
            1,
            format!("unsupported hit object type: {trimmed}"),
        )
    })?;
    match parsed {
        0 | 1 => Ok(parsed),
        other => Err(UrcError::new(
            "syntax",
            1,
            format!("unsupported hit object type: {other}"),
        )),
    }
}
