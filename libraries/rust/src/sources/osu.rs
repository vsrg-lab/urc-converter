//! osu!mania (`.osu`) source parser and converter.

use std::cmp::{max, min};

use super::shared::{build_timing, check_hold_overlap};
use crate::error::{Result, UrcError};
use crate::model::{Chart, Judgment, Layout, Metadata, Note, NoteType, Version};

const JUDGMENT_RATES: [f64; 6] = [100.0, 100.0, 66.67, 33.33, 16.67, 0.0];
const KEY_MIN: u64 = 1;
const KEY_MAX: u64 = 18;

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

/// Parses `.osu` text into a source model.
pub fn parse_osu(text: &str) -> Result<OsuBeatmap> {
    let mut beatmap = OsuBeatmap::default();
    let mut section: Option<String> = None;

    for (offset, raw) in text
        .strip_prefix('\u{feff}')
        .unwrap_or(text)
        .lines()
        .enumerate()
    {
        let line_no = offset as u32 + 1;
        let line = raw.trim();

        if line.starts_with('[') && line.ends_with(']') {
            section = Some(line[1..line.len() - 1].to_owned());
            continue;
        }

        if line.is_empty() || line.starts_with("//") || section.is_none() {
            continue;
        }

        match section.as_deref() {
            Some("General") => general(&mut beatmap, line, line_no)?,
            Some("Metadata") => metadata(&mut beatmap, line),
            Some("Difficulty") => difficulty(&mut beatmap, line, line_no)?,
            Some("TimingPoints") => beatmap.timing_points.push(timing_point(line, line_no)?),
            Some("HitObjects") => beatmap.hit_objects.push(hit_object(line, line_no)?),
            _ => {}
        }
    }

    Ok(beatmap)
}

/// Maps an osu!mania beatmap onto a URC chart.
pub fn convert_osu(beatmap: &OsuBeatmap) -> Result<Chart> {
    if beatmap.mode != 3 {
        return Err(UrcError::new(
            "unsupported-version",
            1,
            format!("unsupported game mode: {}", beatmap.mode),
        ));
    }

    let circle_size = beatmap
        .circle_size
        .ok_or_else(|| UrcError::new("syntax", 1, "missing CircleSize"))?;
    if circle_size != circle_size.trunc() {
        return Err(UrcError::new(
            "syntax",
            1,
            format!("CircleSize must be an integer: {circle_size}"),
        ));
    }

    let keys = circle_size as u64;
    if !(KEY_MIN..=KEY_MAX).contains(&keys) {
        return Err(UrcError::new(
            "syntax",
            1,
            format!("CircleSize out of range: {keys}"),
        ));
    }

    if beatmap
        .timing_points
        .iter()
        .any(|point| point.beat_length == 0.0)
    {
        return Err(UrcError::new(
            "syntax",
            1,
            "timing point with zero beat length",
        ));
    }

    let first_note_time = beatmap
        .hit_objects
        .iter()
        .map(|obj| obj.time)
        .min()
        .unwrap_or(0);

    let bpm_points: Vec<(i64, f64, u64)> = beatmap
        .timing_points
        .iter()
        .filter(|point| point.uninherited)
        .map(|point| (point.time, 60000.0 / point.beat_length, point.meter))
        .collect();
    let sv_points: Vec<(i64, f64)> = beatmap
        .timing_points
        .iter()
        .filter(|point| !point.uninherited)
        .map(|point| (point.time, -100.0 / point.beat_length))
        .collect();

    let timing = build_timing(&bpm_points, &sv_points, first_note_time, ".osu")?;

    let mut notes: Vec<Note> = Vec::new();
    for obj in &beatmap.hit_objects {
        let lane = min(max(obj.x * keys as i64 / 512, 0), keys as i64 - 1) as u32;

        if obj.is_hold {
            let end_time = obj.end_time.unwrap_or_default();
            if end_time < obj.time {
                return Err(UrcError::new(
                    "syntax",
                    1,
                    format!("hold ends before it starts: {end_time} < {}", obj.time),
                ));
            }
            notes.push(Note {
                timestamp_ms: obj.time - first_note_time,
                lane,
                note_type: NoteType::Ls,
            });
            notes.push(Note {
                timestamp_ms: end_time - first_note_time,
                lane,
                note_type: NoteType::Le,
            });
        } else {
            notes.push(Note {
                timestamp_ms: obj.time - first_note_time,
                lane,
                note_type: NoteType::N,
            });
        }
    }

    check_hold_overlap(&notes)?;

    let judgment = beatmap.overall_difficulty.map(|od| Judgment {
        windows: [16.5]
            .into_iter()
            .chain(
                [64.0, 97.0, 127.0, 151.0, 188.0]
                    .into_iter()
                    .map(|base| base - 3.0 * od + 0.5),
            )
            .collect(),
        rates: JUDGMENT_RATES.to_vec(),
    });

    let title = beatmap
        .title_unicode
        .clone()
        .or_else(|| beatmap.title.clone());
    let artist = beatmap
        .artist_unicode
        .clone()
        .or_else(|| beatmap.artist.clone());
    let missing: Vec<&str> = [
        ("Title", title.as_deref()),
        ("Artist", artist.as_deref()),
        ("Creator", beatmap.creator.as_deref()),
        ("Version", beatmap.version.as_deref()),
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
            original: "osu!mania".to_owned(),
            title: title.unwrap_or_default(),
            artist: artist.unwrap_or_default(),
            creator: beatmap.creator.clone().unwrap_or_default(),
            version: beatmap.version.clone().unwrap_or_default(),
        },
        judgment,
        layout: Layout {
            keys,
            special_keys: 0,
            special_lanes: None,
        },
        timing,
        notes,
    })
}

fn general(beatmap: &mut OsuBeatmap, line: &str, line_no: u32) -> Result<()> {
    if let Some((key, value)) = line.split_once(':') {
        if key.trim() == "Mode" {
            beatmap.mode = to_int(value.trim(), line_no, "Mode")?;
        }
    }
    Ok(())
}

fn metadata(beatmap: &mut OsuBeatmap, line: &str) {
    if let Some((key, value)) = line.split_once(':') {
        let value = value.trim().to_owned();
        match key.trim() {
            "Title" => beatmap.title = Some(value),
            "TitleUnicode" => beatmap.title_unicode = Some(value),
            "Artist" => beatmap.artist = Some(value),
            "ArtistUnicode" => beatmap.artist_unicode = Some(value),
            "Creator" => beatmap.creator = Some(value),
            "Version" => beatmap.version = Some(value),
            _ => {}
        }
    }
}

fn difficulty(beatmap: &mut OsuBeatmap, line: &str, line_no: u32) -> Result<()> {
    if let Some((key, value)) = line.split_once(':') {
        match key.trim() {
            "CircleSize" => {
                beatmap.circle_size = Some(to_float(value.trim(), line_no, "CircleSize")?);
            }
            "OverallDifficulty" => {
                beatmap.overall_difficulty =
                    Some(to_float(value.trim(), line_no, "OverallDifficulty")?);
            }
            _ => {}
        }
    }
    Ok(())
}

fn timing_point(line: &str, line_no: u32) -> Result<OsuTimingPoint> {
    let fields: Vec<&str> = line.split(',').map(str::trim).collect();
    if fields.len() < 2 {
        return Err(UrcError::new(
            "syntax",
            line_no,
            format!("timing point needs at least 2 fields: {line:?}"),
        ));
    }

    let meter = match fields.get(2).filter(|field| !field.is_empty()) {
        Some(field) => to_int(field, line_no, "meter")? as u64,
        None => 4,
    };
    let uninherited = match fields.get(6).filter(|field| !field.is_empty()) {
        Some(field) => to_int(field, line_no, "uninherited")? != 0,
        None => true,
    };

    Ok(OsuTimingPoint {
        time: to_int(fields[0], line_no, "timing time")?,
        beat_length: to_float(fields[1], line_no, "beat length")?,
        meter,
        uninherited,
    })
}

fn hit_object(line: &str, line_no: u32) -> Result<OsuHitObject> {
    let fields: Vec<&str> = line.split(',').map(str::trim).collect();
    if fields.len() < 5 {
        return Err(UrcError::new(
            "syntax",
            line_no,
            format!("hit object needs at least 5 fields: {line:?}"),
        ));
    }

    let x = to_int(fields[0], line_no, "hit object x")?;
    let time = to_int(fields[2], line_no, "hit object time")?;
    let type_bits = to_int(fields[3], line_no, "hit object type")?;

    let is_hold = type_bits & 128 != 0;
    if !is_hold && type_bits & 1 == 0 {
        return Err(UrcError::new(
            "syntax",
            line_no,
            format!("unsupported hit object type: {type_bits}"),
        ));
    }

    let end_time = if is_hold {
        let token = fields.get(5).ok_or_else(|| {
            UrcError::new(
                "syntax",
                line_no,
                format!("hold note needs an end time: {line:?}"),
            )
        })?;
        Some(to_int(
            token.split(':').next().unwrap_or_default().trim(),
            line_no,
            "hold end time",
        )?)
    } else {
        None
    };

    Ok(OsuHitObject {
        x,
        time,
        is_hold,
        end_time,
    })
}

fn to_int(token: &str, line_no: u32, label: &str) -> Result<i64> {
    token
        .parse::<f64>()
        .map(|value| value.round() as i64)
        .map_err(|_| UrcError::new("syntax", line_no, format!("invalid {label}: {token:?}")))
}

fn to_float(token: &str, line_no: u32, label: &str) -> Result<f64> {
    token
        .parse::<f64>()
        .map_err(|_| UrcError::new("syntax", line_no, format!("invalid {label}: {token:?}")))
}
