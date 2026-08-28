//! Scan orchestration and the public parse entry point.

use super::assembly::build;
use super::checks::finalize_section;
use super::fields::dispatch_content;
use super::lex;
use super::state::ParseState;
use crate::error::{Result, UrcError};
use crate::model::{Chart, Version};
use crate::strings::{SECTION_URC, SECTIONS, SYNTAX, UNSUPPORTED_VERSION, rule, section_index};

/// Parses and validates URC text into a [`Chart`].
///
/// Returns the first failure as [`UrcError`] with category `"syntax"`,
/// `"unsupported-version"`, or `"rule:<n>"` (spec rules 1-22).
pub fn parse(text: &str) -> Result<Chart> {
    let text = text.strip_prefix('\u{feff}').unwrap_or(text);
    let lines: Vec<&str> = text.lines().collect();
    let mut state = ParseState::default();

    scan(&lines, &mut state)?;
    build(state, lines.len() as u32 + 1)
}

fn scan(lines: &[&str], state: &mut ParseState) -> Result<()> {
    let mut current = SECTION_URC;
    for (offset, raw) in lines.iter().enumerate() {
        let line_no = (offset + 1) as u32;
        let text = raw.trim();

        if offset == 0 {
            header(text, line_no, state)?;
        } else if text.is_empty() || text.starts_with('#') {
            continue;
        } else if text.starts_with('@') {
            finalize_section(state, current, line_no)?;
            current = section(text, line_no, state)?;
        } else {
            dispatch_content(state, current, text, line_no)?;
        }
    }

    finalize_section(state, current, lines.len() as u32 + 1)
}

fn header(text: &str, line: u32, state: &mut ParseState) -> Result<()> {
    if !text.starts_with("@URC") {
        return Err(UrcError::new(
            rule(1),
            line,
            "first line must be '@URC <version>'",
        ));
    }

    let malformed = || UrcError::new(SYNTAX, line, format!("malformed @URC header: {text:?}"));
    let Some(rest) = text.strip_prefix("@URC ") else {
        return Err(malformed());
    };
    let Some((major, minor)) = rest.split_once('.') else {
        return Err(malformed());
    };
    if !lex::valid_uint(major) || !lex::valid_uint(minor) {
        return Err(malformed());
    }

    let major = major.parse::<u64>().unwrap_or(u64::MAX);
    let minor = minor.parse::<u64>().unwrap_or(u64::MAX);
    if major != 1 || minor > 1 {
        return Err(UrcError::new(
            UNSUPPORTED_VERSION,
            line,
            format!("unsupported version: {major}.{minor}"),
        ));
    }

    state.version = Version {
        major: major as u32,
        minor: minor as u32,
    };
    Ok(())
}

fn section(name: &str, line: u32, state: &mut ParseState) -> Result<&'static str> {
    let Some(index) = section_index(name) else {
        return Err(UrcError::new(
            SYNTAX,
            line,
            format!("unknown section: {name}"),
        ));
    };

    if state.seen[index] {
        return Err(UrcError::new(
            rule(3),
            line,
            format!("duplicate section: {name}"),
        ));
    }
    if index <= state.last_index {
        return Err(UrcError::new(
            rule(3),
            line,
            format!("section out of order: {name}"),
        ));
    }

    state.seen[index] = true;
    state.last_index = index;
    Ok(SECTIONS[index])
}
