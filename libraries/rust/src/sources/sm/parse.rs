//! Parser for StepMania (`.sm`/`.ssc`) simfiles.

use std::collections::HashMap;

use crate::error::{Result, UrcError};

use super::model::{NoteKind, SmChart, SmFile, SmNote, Timing};

const ROWS_PER_BEAT: f64 = 48.0;

fn step_lanes() -> HashMap<&'static str, u32> {
    HashMap::from([
        ("dance-single", 4),
        ("dance-double", 8),
        ("dance-solo", 6),
        ("dance-threepanel", 3),
        ("pump-single", 5),
        ("pump-halfdouble", 6),
        ("pump-double", 10),
        ("kb7-single", 7),
        ("techno-single4", 4),
        ("techno-single5", 5),
        ("techno-single8", 8),
        ("techno-double4", 8),
        ("techno-double5", 10),
        ("techno-double8", 16),
        ("maniax-single", 4),
        ("maniax-double", 8),
        ("pnm-five", 5),
        ("pnm-nine", 9),
        ("para-single", 5),
        ("ds3ddx-single", 8),
        ("ez2-single", 5),
        ("ez2-double", 10),
        ("ez2-real", 7),
        ("kickbox-human", 4),
        ("kickbox-quadarm", 4),
        ("kickbox-insect", 6),
        ("kickbox-arachnid", 8),
    ])
}

/// Track count for a steps type; unsupported types are an error.
pub fn resolve_lanes(steps_type: &str) -> Result<u32> {
    let alias = match steps_type {
        "ez2-single-hard" => "ez2-single",
        "para" => "para-single",
        other => other,
    };
    let shown = if steps_type.is_empty() {
        "(missing)"
    } else {
        steps_type
    };
    step_lanes()
        .get(alias)
        .copied()
        .ok_or_else(|| UrcError::new("unsupported-version", 1, format!("unsupported steps type: {shown}")))
}

/// Parses a `.sm` or `.ssc` simfile into its source model.
pub fn parse_sm(text: &str) -> Result<SmFile> {
    let mut simfile = SmFile::default();
    let mut chart: Option<SmChart> = None;

    for params in tokenize(text) {
        let tag = params[0].to_uppercase();
        let value = params.get(1).cloned().unwrap_or_default();

        if tag == "NOTEDATA" {
            chart = Some(SmChart::default());
            continue;
        }
        if tag == "NOTES" || tag == "NOTES2" {
            if let Some(mut open) = chart.take() {
                let lanes = resolve_lanes(&open.steps_type)?;
                open.notes = parse_note_data(&value, lanes)?;
                simfile.charts.push(open);
            } else if params.len() >= 7 {
                let mut block = SmChart {
                    steps_type: params[1].trim().to_string(),
                    description: params[2].trim().to_string(),
                    difficulty: params[3].trim().to_string(),
                    credit: params[2].trim().to_string(),
                    ..Default::default()
                };
                let lanes = resolve_lanes(&block.steps_type)?;
                block.notes = parse_note_data(&params[6], lanes)?;
                simfile.charts.push(block);
            }
            continue;
        }

        match chart {
            None => song_tag(&mut simfile, &tag, &value)?,
            Some(ref mut open) => chart_tag(&simfile, open, &tag, &value)?,
        }
    }

    if simfile.charts.is_empty() {
        return Err(UrcError::new("syntax", 1, "no chart in simfile"));
    }
    Ok(simfile)
}

fn song_tag(simfile: &mut SmFile, tag: &str, value: &str) -> Result<()> {
    match tag {
        "TITLE" => simfile.title = value.to_string(),
        "SUBTITLE" => simfile.subtitle = value.to_string(),
        "ARTIST" => simfile.artist = value.to_string(),
        "CREDIT" => simfile.credit = value.to_string(),
        _ => timing_tag(&mut simfile.timing, tag, value)?,
    }
    Ok(())
}

fn chart_tag(simfile: &SmFile, chart: &mut SmChart, tag: &str, value: &str) -> Result<()> {
    match tag {
        "STEPSTYPE" => chart.steps_type = value.trim().to_string(),
        "DESCRIPTION" => chart.description = value.trim().to_string(),
        "DIFFICULTY" => chart.difficulty = value.trim().to_string(),
        "CHARTNAME" => chart.chartname = value.trim().to_string(),
        "CREDIT" => chart.credit = value.to_string(),
        "OFFSET" | "BPMS" | "STOPS" | "FREEZES" | "DELAYS" | "WARPS" | "SCROLLS" | "FAKES"
        | "TIMESIGNATURES" => {
            let timing = chart.timing.get_or_insert_with(|| Timing {
                offset: simfile.timing.offset,
                ..Default::default()
            });
            timing_tag(timing, tag, value)?;
        }
        _ => {}
    }
    Ok(())
}

fn timing_tag(timing: &mut Timing, tag: &str, value: &str) -> Result<()> {
    match tag {
        "OFFSET" => timing.offset = parse_float(value)?,
        "BPMS" => timing.bpms.extend(pairs(value, true)?),
        "STOPS" | "FREEZES" => timing.stops.extend(pairs(value, true)?),
        "DELAYS" => timing.delays.extend(pairs(value, true)?),
        "WARPS" => timing.warps.extend(pairs(value, false)?),
        "SCROLLS" => timing.scrolls.extend(pairs(value, false)?),
        "FAKES" => timing
            .fakes
            .extend(pairs(value, false)?.into_iter().filter(|entry| entry.1 > 0.0)),
        "TIMESIGNATURES" => {
            for parts in expressions(value, 3)? {
                let beat = parse_beat(&parts[0])?;
                let numerator = parse_int(&parts[1])?;
                let denominator = parse_int(&parts[2])?;
                if numerator >= 1 && denominator >= 1 && beat >= 0.0 {
                    timing
                        .timesigs
                        .push((beat, numerator as u64, denominator as u64));
                }
            }
        }
        _ => {}
    }
    Ok(())
}

/// Splits a simfile into MSD values (`#TAG:param:...;`) following MsdFile.
fn tokenize(text: &str) -> Vec<Vec<String>> {
    let chars: Vec<char> = text.chars().collect();
    let mut values: Vec<Vec<String>> = Vec::new();
    let mut params: Vec<String> = Vec::new();
    let mut current = String::new();
    let mut line = String::new();
    let mut reading = false;

    let mut i = 0;
    let n = chars.len();
    while i < n {
        if i + 1 < n && chars[i] == '/' && chars[i + 1] == '/' {
            while i < n && chars[i] != '\n' {
                i += 1;
            }
            continue;
        }
        if reading && chars[i] == '#' {
            if !line.trim_matches([' ', '\t']).is_empty() {
                current.push('#');
                line.push('#');
                i += 1;
                continue;
            }
            params.push(current.trim_end_matches([' ', '\t', '\r', '\n']).to_string());
            values.push(params);
            params = Vec::new();
            current = String::new();
            line = String::new();
            reading = false;
            continue;
        }
        if !reading {
            if chars[i] == '#' {
                reading = true;
                line.clear();
            } else if chars[i] != '\\' {
                i += 1;
                continue;
            } else if i + 1 < n {
                i += 2;
                continue;
            }
            i += 1;
            continue;
        }
        match chars[i] {
            ':' => {
                params.push(std::mem::take(&mut current));
                line.clear();
            }
            ';' => {
                params.push(std::mem::take(&mut current));
                values.push(params);
                params = Vec::new();
                line.clear();
                reading = false;
            }
            '\\' => {
                i += 1;
                if i < n {
                    current.push(chars[i]);
                    line.push(chars[i]);
                }
            }
            ch => {
                current.push(ch);
                line.push(ch);
            }
        }
        if i < n && (chars[i] == '\r' || chars[i] == '\n') {
            line.clear();
        }
        i += 1;
    }

    if reading {
        params.push(current);
    }
    values
}

fn expressions(value: &str, minimum: usize) -> Result<Vec<Vec<String>>> {
    let mut parts = Vec::new();
    for expression in value.split(',') {
        if expression.trim().is_empty() {
            continue;
        }
        let fields: Vec<String> = expression.split('=').map(str::to_string).collect();
        if fields.len() < minimum {
            return Err(UrcError::new(
                "syntax",
                1,
                format!("malformed timing expression: {expression}"),
            ));
        }
        parts.push(fields);
    }
    Ok(parts)
}

fn pairs(value: &str, skip_zero: bool) -> Result<Vec<(f64, f64)>> {
    let mut entries = Vec::new();
    for parts in expressions(value, 2)? {
        if parts.len() != 2 {
            return Err(UrcError::new(
                "syntax",
                1,
                format!("malformed timing expression: {}", parts.join("=")),
            ));
        }
        let beat = parse_beat(&parts[0])?;
        let number = parse_float(&parts[1])?;
        if !skip_zero || number != 0.0 {
            entries.push((beat, number));
        }
    }
    Ok(entries)
}

fn parse_beat(token: &str) -> Result<f64> {
    if token.trim_end().ends_with(['r', 'R']) {
        return Err(UrcError::new(
            "syntax",
            1,
            format!("row-format beats are not supported: {token}"),
        ));
    }
    parse_float(token)
}

fn parse_float(token: &str) -> Result<f64> {
    let text = token.trim();
    let valid = !text.is_empty()
        && text
            .bytes()
            .all(|b| b.is_ascii_digit() || matches!(b, b'+' | b'-' | b'.' | b'e' | b'E'));
    if !valid || text.parse::<f64>().is_err() {
        return Err(UrcError::new("syntax", 1, format!("invalid number: {token}")));
    }
    Ok(text.parse::<f64>().unwrap())
}

fn parse_int(token: &str) -> Result<i64> {
    let text = token.trim();
    text.parse::<i64>()
        .map_err(|_| UrcError::new("syntax", 1, format!("invalid integer: {token}")))
}

fn parse_note_data(data: &str, lanes: u32) -> Result<Vec<SmNote>> {
    let mut notes: Vec<SmNote> = Vec::new();
    let mut open_holds: HashMap<u32, usize> = HashMap::new();

    let mut measure = 0_u64;
    for part in data.split(',') {
        if part.is_empty() {
            continue;
        }
        let content: Vec<&str> = part
            .split('\n')
            .map(|raw| raw.trim_matches([' ', '\t', '\r']))
            .filter(|line| !line.is_empty())
            .collect();
        let total = content.len() as f64;
        for (index, line) in content.iter().enumerate() {
            let row = rows((measure as f64 + index as f64 / total) * 4.0);
            let chars: Vec<char> = line.chars().collect();
            let mut track = 0_u32;
            let mut position = 0_usize;
            while track < lanes && position < chars.len() {
                let ch = chars[position];
                position += 1;
                match ch {
                    '1' => notes.push(SmNote {
                        row,
                        track,
                        kind: NoteKind::Tap,
                        tail_row: None,
                    }),
                    '2' | '4' => {
                        if open_holds.contains_key(&track) {
                            return Err(UrcError::new(
                                "syntax",
                                1,
                                format!("overlapping hold head at row {row}"),
                            ));
                        }
                        notes.push(SmNote {
                            row,
                            track,
                            kind: if ch == '2' { NoteKind::Hold } else { NoteKind::Roll },
                            tail_row: None,
                        });
                        open_holds.insert(track, notes.len() - 1);
                    }
                    '3' => {
                        let index = open_holds
                            .remove(&track)
                            .ok_or_else(|| UrcError::new("syntax", 1, format!("hold tail without a head at row {row}")))?;
                        notes[index].tail_row = Some(row);
                    }
                    'M' => notes.push(SmNote {
                        row,
                        track,
                        kind: NoteKind::Mine,
                        tail_row: None,
                    }),
                    'L' => notes.push(SmNote {
                        row,
                        track,
                        kind: NoteKind::Lift,
                        tail_row: None,
                    }),
                    'F' => notes.push(SmNote {
                        row,
                        track,
                        kind: NoteKind::Fake,
                        tail_row: None,
                    }),
                    _ => {}
                }
                if position < chars.len() && chars[position] == '[' {
                    match chars[position..].iter().position(|&c| c == ']') {
                        Some(end) => position += end + 1,
                        None => position = chars.len(),
                    }
                }
                track += 1;
            }
        }
        measure += 1;
    }

    if !open_holds.is_empty() {
        return Err(UrcError::new("syntax", 1, "hold note without a tail"));
    }
    Ok(notes)
}

fn rows(beats: f64) -> i64 {
    let value = beats * ROWS_PER_BEAT;
    if value >= 0.0 {
        (value + 0.5) as i64
    } else {
        (value - 0.5) as i64
    }
}
