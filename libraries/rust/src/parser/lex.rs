//! Scalar and list token parsing for URC field values.

use crate::error::{Result, UrcError};
use crate::model::Meter;
use crate::strings::{SYNTAX, rule};

pub(super) fn valid_uint(token: &str) -> bool {
    !token.is_empty() && token.bytes().all(|b| b.is_ascii_digit())
}

pub(super) fn parse_int(token: &str, line: u32) -> Result<i64> {
    let body = token.strip_prefix('-').unwrap_or(token);
    if !valid_uint(body) {
        return Err(UrcError::new(
            SYNTAX,
            line,
            format!("invalid integer: {token:?}"),
        ));
    }
    token
        .parse::<i64>()
        .map_err(|_| UrcError::new(SYNTAX, line, format!("invalid integer: {token:?}")))
}

pub(super) fn valid_float(token: &str) -> bool {
    let body = token.strip_prefix('-').unwrap_or(token);
    match body.split_once('.') {
        Some((int_part, frac)) => valid_uint(int_part) && valid_uint(frac),
        None => valid_uint(body),
    }
}

pub(super) fn parse_float(token: &str, line: u32) -> Result<f64> {
    if !valid_float(token) {
        return Err(UrcError::new(
            SYNTAX,
            line,
            format!("invalid float: {token:?}"),
        ));
    }
    token
        .parse::<f64>()
        .map_err(|_| UrcError::new(SYNTAX, line, format!("invalid float: {token:?}")))
}

pub(super) fn float_list(value: &str, line: u32) -> Result<Vec<f64>> {
    value
        .split(',')
        .map(|token| parse_float(token.trim(), line))
        .collect()
}

pub(super) fn layout_type(value: &str, line: u32) -> Result<(u64, u64)> {
    let malformed = || UrcError::new(SYNTAX, line, format!("invalid Type value: {value:?}"));
    let (keys, special) = match value.split_once('+') {
        Some((keys, special)) => (keys, Some(special)),
        None => (value, None),
    };
    if !valid_uint(keys) {
        return Err(malformed());
    }
    let had_special = special.is_some();
    let special = match special {
        None => 0,
        Some(text) if valid_uint(text) => text.parse::<u64>().unwrap_or(u64::MAX),
        Some(_) => return Err(malformed()),
    };
    let keys = keys.parse::<u64>().unwrap_or(u64::MAX);
    if keys < 1 || (had_special && special < 1) {
        return Err(UrcError::new(SYNTAX, line, "Type values must be positive"));
    }
    Ok((keys, special))
}

pub(super) fn parse_meter(token: &str, line: u32) -> Result<Meter> {
    let invalid = || UrcError::new(rule(17), line, format!("invalid meter: {token:?}"));
    let Some((beats, note_value)) = token.split_once('/') else {
        return Err(invalid());
    };
    if !valid_uint(beats) || !valid_uint(note_value) {
        return Err(invalid());
    }
    let beats = beats.parse::<u64>().unwrap_or(u64::MAX);
    let note_value = note_value.parse::<u64>().unwrap_or(u64::MAX);
    if beats < 1 || note_value < 1 {
        return Err(invalid());
    }
    Ok(Meter { beats, note_value })
}
