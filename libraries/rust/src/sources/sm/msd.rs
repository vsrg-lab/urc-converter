//! MSD (Music Sync Document) parser and tokenizer for StepMania files.

use crate::error::{Result, UrcError};

/// Splits a simfile into MSD values (`#TAG:param:...;`) following MsdFile.
pub(crate) fn tokenize(text: &str) -> Vec<Vec<String>> {
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

pub(crate) fn expressions(value: &str, minimum: usize) -> Result<Vec<Vec<String>>> {
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

pub(crate) fn pairs(value: &str, skip_zero: bool) -> Result<Vec<(f64, f64)>> {
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

pub(crate) fn parse_beat(token: &str) -> Result<f64> {
    if token.trim_end().ends_with(['r', 'R']) {
        return Err(UrcError::new(
            "syntax",
            1,
            format!("row-format beats are not supported: {token}"),
        ));
    }
    parse_float(token)
}

pub(crate) fn parse_float(token: &str) -> Result<f64> {
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

pub(crate) fn parse_int(token: &str) -> Result<i64> {
    let text = token.trim();
    text.parse::<i64>()
        .map_err(|_| UrcError::new("syntax", 1, format!("invalid integer: {token}")))
}
