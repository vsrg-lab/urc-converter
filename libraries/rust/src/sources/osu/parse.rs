//! Parser for `.osu` text.

use crate::error::{Result, UrcError};

use super::model::{OsuBeatmap, OsuHitObject, OsuTimingPoint};

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

fn general(beatmap: &mut OsuBeatmap, line: &str, line_no: u32) -> Result<()> {
    if let Some((key, value)) = line.split_once(':')
        && key.trim() == "Mode"
    {
        beatmap.mode = to_int(value.trim(), line_no, "Mode")?;
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
