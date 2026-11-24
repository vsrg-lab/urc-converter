//! Playback rate scaling for URC charts.

use std::collections::BTreeMap;

use crate::error::{Result, UrcError};
use crate::model::{Chart, Note, NoteType, RateResult, TimingPoint, UrcWarning, WarningCode};
use crate::strings::INVALID_RATE;

const MIN_SCALED_RATE: i64 = 50;
const MAX_SCALED_RATE: i64 = 300;
const STEP_TOLERANCE: f64 = 1e-9;

fn round_ms(value: f64) -> i64 {
    if value >= 0.0 {
        (value + 0.5) as i64
    } else {
        (value - 0.5) as i64
    }
}

fn validate_rate(rate: f64) -> Result<i64> {
    if !rate.is_finite() {
        return Err(UrcError::new(
            INVALID_RATE,
            1,
            "playback rate must be between 0.50 and 3.00 in 0.01 steps",
        ));
    }
    let scaled = (rate * 100.0).round() as i64;
    if (rate * 100.0 - scaled as f64).abs() > STEP_TOLERANCE
        || !(MIN_SCALED_RATE..=MAX_SCALED_RATE).contains(&scaled)
    {
        return Err(UrcError::new(
            INVALID_RATE,
            1,
            "playback rate must be between 0.50 and 3.00 in 0.01 steps",
        ));
    }
    Ok(scaled)
}

/// Scales a chart's playback rate within 0.50x to 3.00x in 0.01 steps.
///
/// Timestamps are rounded to integer milliseconds and BPMs are multiplied by
/// the rate. Same-lane events whose interval or hold length falls below 1ms are
/// pushed forward to maintain at least a 1ms gap. Warnings are emitted only
/// when the input chart already contained a sub-1ms gap on the same lane.
pub fn scale_rate(chart: &Chart, rate: f64) -> Result<RateResult> {
    let scaled = validate_rate(rate)?;
    let scaled_f64 = scaled as f64;

    let mut shifted_timing: BTreeMap<i64, TimingPoint> = BTreeMap::new();
    for point in &chart.timing {
        let timestamp_ms = round_ms(point.timestamp_ms as f64 * 100.0 / scaled_f64);
        let bpm = (point.bpm * scaled_f64) / 100.0;
        shifted_timing.insert(
            timestamp_ms,
            TimingPoint {
                timestamp_ms,
                bpm,
                meter: point.meter,
                multiplier: point.multiplier,
            },
        );
    }
    let timing: Vec<TimingPoint> = shifted_timing.into_values().collect();

    let mut ordered_input = chart.notes.clone();
    ordered_input.sort_by_key(|note| note.timestamp_ms);
    let mut lanes: BTreeMap<u32, Vec<Note>> = BTreeMap::new();
    for note in ordered_input {
        lanes.entry(note.lane).or_default().push(note);
    }

    let mut adjusted_notes: Vec<Note> = Vec::with_capacity(chart.notes.len());
    let mut warnings: Vec<UrcWarning> = Vec::new();

    for (lane, lane_notes) in lanes {
        let mut prev_orig_ms: Option<i64> = None;
        let mut prev_adjusted_ms: Option<i64> = None;
        let mut prev_type: Option<NoteType> = None;

        for note in lane_notes {
            let scaled_ms = round_ms(note.timestamp_ms as f64 * 100.0 / scaled_f64);
            let adjusted_ms = if let Some(prev_adj) = prev_adjusted_ms
                && scaled_ms <= prev_adj
            {
                let next_ms = prev_adj + 1;
                if let Some(prev_orig) = prev_orig_ms
                    && note.timestamp_ms <= prev_orig
                {
                    let is_hold =
                        prev_type == Some(NoteType::Ls) && note.note_type == NoteType::Le;
                    let (code, message) = if is_hold {
                        (
                            WarningCode::HoldLengthClamped,
                            format!(
                                "lane {lane}: hold end at {scaled_ms}ms adjusted to {next_ms}ms (minimum 1ms duration)"
                            ),
                        )
                    } else {
                        (
                            WarningCode::NoteGapClamped,
                            format!(
                                "lane {lane}: note at {scaled_ms}ms adjusted to {next_ms}ms (minimum 1ms gap)"
                            ),
                        )
                    };
                    warnings.push(UrcWarning {
                        code,
                        lane,
                        original_ms: scaled_ms,
                        adjusted_ms: next_ms,
                        message,
                    });
                }
                next_ms
            } else {
                scaled_ms
            };

            adjusted_notes.push(Note {
                timestamp_ms: adjusted_ms,
                lane,
                note_type: note.note_type,
            });
            prev_orig_ms = Some(note.timestamp_ms);
            prev_adjusted_ms = Some(adjusted_ms);
            prev_type = Some(note.note_type);
        }
    }

    adjusted_notes.sort_by_key(|note| (note.timestamp_ms, note.lane));
    warnings.sort_by_key(|warning| (warning.adjusted_ms, warning.lane));

    Ok(RateResult {
        chart: Chart {
            format_version: chart.format_version,
            metadata: chart.metadata.clone(),
            judgment: chart.judgment.clone(),
            layout: chart.layout.clone(),
            timing,
            notes: adjusted_notes,
        },
        warnings,
    })
}
