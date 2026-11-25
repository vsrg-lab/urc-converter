//! MSD (Music Sync Document) parser and tokenizer for StepMania files.

use crate::error::{Result, UrcError};

/// Splits a simfile into MSD values (`#TAG:param:...;`) following MsdFile.
pub(crate) fn tokenize(text: &str) -> Vec<Vec<String>> {
    let bytes = text.as_bytes();
    let mut values: Vec<Vec<String>> = Vec::new();
    let mut params: Vec<String> = Vec::new();
    let mut current = String::new();
    let mut line_has_visible = false;
    let mut reading = false;

    let mut i = 0;
    let n = bytes.len();
    while i < n {
        let byte = bytes[i];
        if byte == b'/' && i + 1 < n && bytes[i + 1] == b'/' {
            while i < n && bytes[i] != b'\n' && bytes[i] != b'\r' {
                i += 1;
            }
            continue;
        }
        if reading && byte == b'#' {
            if line_has_visible {
                current.push('#');
                i += 1;
                continue;
            }
            params.push(current.trim_end_matches([' ', '\t', '\r', '\n']).to_string());
            values.push(params);
            params = Vec::new();
            current = String::new();
            line_has_visible = false;
            reading = false;
            continue;
        }
        if !reading {
            if byte == b'#' {
                reading = true;
                line_has_visible = false;
                i += 1;
            } else if byte == b'\\' && i + 1 < n {
                i += 1;
                if let Some(ch) = text[i..].chars().next() {
                    i += ch.len_utf8();
                }
            } else {
                i += 1;
            }
            continue;
        }
        match byte {
            b':' => {
                params.push(std::mem::take(&mut current));
                line_has_visible = false;
                i += 1;
            }
            b';' => {
                params.push(std::mem::take(&mut current));
                values.push(params);
                params = Vec::new();
                line_has_visible = false;
                reading = false;
                i += 1;
            }
            b'\\' => {
                i += 1;
                if let Some(ch) = text[i..].chars().next() {
                    current.push(ch);
                    if ch == '\r' || ch == '\n' {
                        line_has_visible = false;
                    } else if ch != ' ' && ch != '\t' {
                        line_has_visible = true;
                    }
                    i += ch.len_utf8();
                }
            }
            _ => {
                let start = i;
                while i < n {
                    let b = bytes[i];
                    if matches!(b, b'#' | b':' | b';' | b'\\')
                        || (b == b'/' && i + 1 < n && bytes[i + 1] == b'/')
                    {
                        break;
                    }
                    if b == b'\r' || b == b'\n' {
                        line_has_visible = false;
                    } else if b != b' ' && b != b'\t' {
                        line_has_visible = true;
                    }
                    i += 1;
                }
                current.push_str(&text[start..i]);
            }
        }
    }

    if reading {
        params.push(current);
        values.push(params);
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
    if !valid {
        return Err(UrcError::new("syntax", 1, format!("invalid number: {token}")));
    }
    let parsed = text
        .parse::<f64>()
        .map_err(|_| UrcError::new("syntax", 1, format!("invalid number: {token}")))?;
    if !parsed.is_finite() {
        return Err(UrcError::new("syntax", 1, format!("invalid number: {token}")));
    }
    Ok(parsed)
}

pub(crate) fn parse_int(token: &str) -> Result<i64> {
    let text = token.trim();
    text.parse::<i64>()
        .map_err(|_| UrcError::new("syntax", 1, format!("invalid integer: {token}")))
}
