//! Finalize-time semantic checks (Phase A completion and Phase B).

use super::state::ParseState;
use crate::error::{Result, UrcError};
use crate::strings::{
    METADATA_FIELDS, SECTION_JUDGMENT, SECTION_LAYOUT, SECTION_METADATA, SECTION_TIMING, rule,
};

pub(super) fn finalize_section(state: &mut ParseState, section: &str, line: u32) -> Result<()> {
    match section {
        SECTION_METADATA => check_metadata_complete(state, line),
        SECTION_JUDGMENT => check_judgment(state, line),
        SECTION_LAYOUT => check_layout(state, line),
        SECTION_TIMING if state.timing.is_empty() => Err(UrcError::new(
            rule(14),
            line,
            "first timing point must be at timestamp 0",
        )),
        _ => Ok(()),
    }
}

fn check_metadata_complete(state: &mut ParseState, line: u32) -> Result<()> {
    for field in METADATA_FIELDS {
        if !state.metadata.iter().any(|(name, _)| name == field) {
            return Err(UrcError::new(
                rule(4),
                line,
                format!("Metadata is missing field: {field}"),
            ));
        }
    }
    Ok(())
}

fn check_judgment(state: &mut ParseState, line: u32) -> Result<()> {
    let (Some(windows), Some(rates)) = (&state.windows, &state.rates) else {
        return Err(UrcError::new(
            rule(4),
            line,
            "Judgment requires both Window and Rate",
        ));
    };

    if windows.len() != rates.len() {
        return Err(UrcError::new(
            rule(7),
            line,
            "Window and Rate must have the same count",
        ));
    }
    if windows.windows(2).any(|pair| pair[1] < pair[0]) {
        return Err(UrcError::new(
            rule(8),
            line,
            "Window values must be ascending",
        ));
    }
    if rates.windows(2).any(|pair| pair[1] > pair[0]) {
        return Err(UrcError::new(
            rule(9),
            line,
            "Rate values must be descending",
        ));
    }
    if rates.iter().any(|&rate| !(0.0..=100.0).contains(&rate)) {
        return Err(UrcError::new(
            rule(10),
            line,
            "Rate values must be in 0-100",
        ));
    }
    Ok(())
}

fn check_layout(state: &mut ParseState, line: u32) -> Result<()> {
    let Some((keys, special_keys)) = state.layout_type else {
        return Err(UrcError::new(
            rule(4),
            line,
            "Layout is missing field: Type",
        ));
    };
    if !state.special_seen {
        return Err(UrcError::new(
            rule(4),
            line,
            "Layout is missing field: Special",
        ));
    }

    let Some(lanes) = &state.special else {
        return Ok(());
    };
    let total = keys + special_keys;
    for &lane in lanes {
        if lane < 0 || lane as u64 >= total {
            return Err(UrcError::new(
                rule(12),
                line,
                format!("special lane out of range: {lane}"),
            ));
        }
    }

    let mut unique = lanes.clone();
    unique.sort_unstable();
    unique.dedup();
    if unique.len() != lanes.len() {
        return Err(UrcError::new(rule(13), line, "duplicate special lanes"));
    }
    Ok(())
}
