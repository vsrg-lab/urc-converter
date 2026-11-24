//! Parser for StepMania (`.sm`/`.ssc`) simfiles.

use std::collections::HashMap;

use crate::error::{Result, UrcError};

use super::model::{SmChart, SmFile, Timing};
use super::msd::{expressions, pairs, parse_beat, parse_float, parse_int, tokenize};
use super::notes::parse_note_data;

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

struct PendingChart {
    chart: SmChart,
    raw_notes: Option<String>,
}

fn finalize_pending(simfile: &mut SmFile, pending: Option<PendingChart>) -> Result<()> {
    if let Some(PendingChart {
        mut chart,
        raw_notes: Some(raw),
    }) = pending
    {
        let lanes = resolve_lanes(&chart.steps_type)?;
        chart.notes = parse_note_data(&raw, lanes)?;
        simfile.charts.push(chart);
    }
    Ok(())
}

/// Parses a `.sm` or `.ssc` simfile into its source model.
pub fn parse_sm(text: &str) -> Result<SmFile> {
    let mut simfile = SmFile::default();
    let mut pending: Option<PendingChart> = None;

    for params in tokenize(text) {
        let tag = params[0].to_uppercase();
        let value = params.get(1).cloned().unwrap_or_default();

        if tag == "NOTEDATA" {
            finalize_pending(&mut simfile, pending.take())?;
            pending = Some(PendingChart {
                chart: SmChart::default(),
                raw_notes: None,
            });
            continue;
        }
        if tag == "NOTES" || tag == "NOTES2" {
            if let Some(ref mut open) = pending
                && params.len() < 7
            {
                open.raw_notes = Some(value);
            } else if params.len() >= 7 {
                finalize_pending(&mut simfile, pending.take())?;
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

        match pending {
            None => song_tag(&mut simfile, &tag, &value)?,
            Some(ref mut open) => chart_tag(&simfile, &mut open.chart, &tag, &value)?,
        }
    }

    finalize_pending(&mut simfile, pending.take())?;
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
            if value.trim().is_empty() {
                return Ok(());
            }
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
        "OFFSET" => {
            timing.offset = if value.trim().is_empty() {
                0.0
            } else {
                parse_float(value)?
            };
        }
        "BPMS" => timing.bpms.extend(pairs(value, true)?),
        "STOPS" | "FREEZES" => timing.stops.extend(pairs(value, true)?),
        "DELAYS" => timing
            .delays
            .extend(pairs(value, false)?.into_iter().filter(|entry| entry.1 > 0.0)),
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

