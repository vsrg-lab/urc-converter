//! Per-section field line parsing (Phase A content handlers).

use super::lex;
use super::state::{ParseState, RawNote};
use crate::error::{Result, UrcError};
use crate::model::TimingPoint;
use crate::strings::{
    JUDGMENT_FIELD_RATE, JUDGMENT_FIELD_WINDOW, LAYOUT_FIELD_SPECIAL, LAYOUT_FIELD_TYPE,
    METADATA_FIELDS, SECTION_JUDGMENT, SECTION_LAYOUT, SECTION_METADATA, SECTION_NOTES,
    SECTION_TIMING, SPECIAL_NONE, SYNTAX, rule,
};

pub(super) fn dispatch_content(
    state: &mut ParseState,
    section: &str,
    text: &str,
    line: u32,
) -> Result<()> {
    match section {
        SECTION_METADATA => metadata_field(state, text, line),
        SECTION_JUDGMENT => judgment_field(state, text, line),
        SECTION_LAYOUT => layout_field(state, text, line),
        SECTION_TIMING => timing_point(state, text, line),
        SECTION_NOTES => note_line(state, text, line),
        _ => Err(UrcError::new(
            SYNTAX,
            line,
            "unexpected content after @URC header",
        )),
    }
}

fn split_field(text: &str) -> Option<(&str, &str)> {
    let (name, value) = text.split_once(':')?;
    Some((name.trim(), value))
}

fn metadata_field(state: &mut ParseState, text: &str, line: u32) -> Result<()> {
    let Some((name, value)) = split_field(text) else {
        return Err(UrcError::new(
            SYNTAX,
            line,
            format!("expected 'Field: Value', got: {text:?}"),
        ));
    };

    if !METADATA_FIELDS.contains(&name) {
        return Err(UrcError::new(
            rule(6),
            line,
            format!("unknown metadata field: {name}"),
        ));
    }
    if state.metadata.iter().any(|(known, _)| known == name) {
        return Err(UrcError::new(
            SYNTAX,
            line,
            format!("duplicate metadata field: {name}"),
        ));
    }

    let value = value.trim();
    if value.is_empty() {
        return Err(UrcError::new(
            rule(5),
            line,
            format!("metadata field has no value: {name}"),
        ));
    }

    state.metadata.push((name.to_string(), value.to_string()));
    Ok(())
}

fn judgment_field(state: &mut ParseState, text: &str, line: u32) -> Result<()> {
    let Some((name, value)) = split_field(text) else {
        return Err(UrcError::new(
            SYNTAX,
            line,
            format!("expected 'Field: values', got: {text:?}"),
        ));
    };

    match name {
        JUDGMENT_FIELD_WINDOW => {
            if state.windows.is_some() {
                return Err(UrcError::new(
                    SYNTAX,
                    line,
                    "duplicate judgment field: Window",
                ));
            }
            state.windows = Some(lex::float_list(value, line)?);
        }
        JUDGMENT_FIELD_RATE => {
            if state.rates.is_some() {
                return Err(UrcError::new(
                    SYNTAX,
                    line,
                    "duplicate judgment field: Rate",
                ));
            }
            state.rates = Some(lex::float_list(value, line)?);
        }
        _ => {
            return Err(UrcError::new(
                rule(6),
                line,
                format!("unknown judgment field: {name}"),
            ));
        }
    }
    Ok(())
}

fn layout_field(state: &mut ParseState, text: &str, line: u32) -> Result<()> {
    let Some((name, value)) = split_field(text) else {
        return Err(UrcError::new(
            SYNTAX,
            line,
            format!("expected 'Field: Value', got: {text:?}"),
        ));
    };

    match name {
        LAYOUT_FIELD_TYPE => {
            if state.layout_type.is_some() {
                return Err(UrcError::new(SYNTAX, line, "duplicate layout field: Type"));
            }
            state.layout_type = Some(lex::layout_type(value.trim(), line)?);
        }
        LAYOUT_FIELD_SPECIAL => {
            if state.special_seen {
                return Err(UrcError::new(
                    SYNTAX,
                    line,
                    "duplicate layout field: Special",
                ));
            }
            if value.trim() == SPECIAL_NONE {
                state.special = None;
            } else {
                let mut lanes = Vec::new();
                for token in value.split(',') {
                    let token = token.trim();
                    if token.is_empty() {
                        return Err(UrcError::new(SYNTAX, line, "empty lane in Special list"));
                    }
                    lanes.push(lex::parse_int(token, line)?);
                }
                state.special = Some(lanes);
            }
            state.special_seen = true;
        }
        _ => {
            return Err(UrcError::new(
                rule(6),
                line,
                format!("unknown layout field: {name}"),
            ));
        }
    }
    Ok(())
}

fn timing_point(state: &mut ParseState, text: &str, line: u32) -> Result<()> {
    let fields: Vec<&str> = text.split(',').map(str::trim).collect();
    if fields.len() != 3 && fields.len() != 4 {
        return Err(UrcError::new(
            SYNTAX,
            line,
            format!("timing point needs 3 or 4 fields, got {}", fields.len()),
        ));
    }
    let timestamp = lex::parse_int(fields[0], line)?;
    let bpm = lex::parse_float(fields[1], line)?;
    let meter = lex::parse_meter(fields[2], line)?;
    let multiplier = if fields.len() == 4 && !fields[3].is_empty() {
        Some(lex::parse_float(fields[3], line)?)
    } else {
        None
    };
    match state.timing.last() {
        None if timestamp != 0 => {
            return Err(UrcError::new(
                rule(14),
                line,
                "first timing point must be at timestamp 0",
            ));
        }
        Some(last) if timestamp <= last.timestamp_ms => {
            return Err(UrcError::new(
                rule(15),
                line,
                "timing timestamps must be strictly ascending",
            ));
        }
        _ => {}
    }
    if bpm <= 0.0 {
        return Err(UrcError::new(rule(16), line, "bpm must be positive"));
    }
    state.timing.push(TimingPoint {
        timestamp_ms: timestamp,
        bpm,
        meter,
        multiplier,
    });
    Ok(())
}

fn note_line(state: &mut ParseState, text: &str, line: u32) -> Result<()> {
    let fields: Vec<&str> = text.split(',').map(str::trim).collect();
    if fields.len() != 3 {
        return Err(UrcError::new(
            SYNTAX,
            line,
            format!("note needs 3 fields, got {}", fields.len()),
        ));
    }
    state.notes.push(RawNote {
        timestamp: lex::parse_int(fields[0], line)?,
        lane: lex::parse_int(fields[1], line)?,
        type_token: fields[2].to_string(),
        line,
    });
    Ok(())
}
