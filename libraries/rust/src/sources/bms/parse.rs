//! Parser for BMS-family bytes.

use std::collections::BTreeMap;

use crate::error::{Result, UrcError};

use super::model::BmsChart;

/// Options for BMS parsing and random branch resolution.
#[derive(Debug, Clone, Default)]
pub struct BmsParseOptions<'a> {
    pub pms: bool,
    pub seed: Option<i64>,
    pub branches: Option<&'a [u64]>,
}
use super::random::JavaRandom;

/// Parses BMS-family bytes into a source model.
pub fn parse_bms(data: &[u8], options: &BmsParseOptions) -> Result<BmsChart> {
    let text = decode_bytes(data)?;
    let normalized = text
        .trim_start_matches('\u{feff}')
        .replace("\r\n", "\n")
        .replace('\r', "\n");
    let base = scan_base(&normalized)?;

    let mut chart = BmsChart {
        pms: options.pms,
        base,
        title: None,
        artist: None,
        play_level: None,
        bpm: None,
        lntype: 1,
        lnobj: None,
        bpm_defs: BTreeMap::new(),
        stop_defs: BTreeMap::new(),
        scroll_defs: BTreeMap::new(),
        rates: BTreeMap::new(),
        measures: BTreeMap::new(),
    };

    let mut random_gen = options.seed.map(JavaRandom::new);
    let mut frames: Vec<(bool, bool)> = Vec::new();
    let mut random_value: Option<i64> = None;
    let mut branch_index = 0;

    for (offset, raw) in normalized.split('\n').enumerate() {
        let line_no = offset as u32 + 1;
        let line = raw.trim();
        if !line.starts_with('#') {
            continue;
        }
        let head = &line[1..];
        let hb = head.as_bytes();

        if hb.len() >= 6
            && hb[0..3].iter().all(u8::is_ascii_digit)
            && hb[3].is_ascii_alphanumeric()
            && hb[4].is_ascii_alphanumeric()
            && hb[5] == b':'
        {
            if !frames.iter().any(|frame| frame.0) {
                let measure = head[..3].parse::<i64>().unwrap();
                let channel = head[3..5].to_ascii_uppercase();
                let payload = &head[6..];
                add_message(&mut chart, measure, &channel, payload, line_no)?;
            }
            continue;
        }

        let (command, argument) = split_command(head);
        let word = command.to_ascii_uppercase();

        if word == "RANDOM" {
            let count = parse_int(argument, line_no, "#RANDOM")?;
            if count < 1 {
                return Err(UrcError::new(
                    "syntax",
                    line_no,
                    format!("#RANDOM count must be >= 1: {count}"),
                ));
            }
            let pick = if let Some(branches) = options.branches
                && branch_index < branches.len()
            {
                let p = branches[branch_index];
                if p < 1 || p > count as u64 {
                    return Err(UrcError::new(
                        "syntax",
                        line_no,
                        format!("branch pick out of range: {p}"),
                    ));
                }
                p as i64
            } else if let Some(ref mut rng) = random_gen {
                (rng.next_int(count as u64) + 1) as i64
            } else {
                1
            };

            random_value = Some(pick);
            branch_index += 1;
        } else if word == "IF" {
            let val =
                random_value.ok_or_else(|| UrcError::new("syntax", line_no, "unmatched #IF"))?;
            let condition = parse_int(argument, line_no, "#IF")?;
            frames.push((val != condition, val == condition));
        } else if word == "ELSEIF" {
            let last = frames
                .last_mut()
                .ok_or_else(|| UrcError::new("syntax", line_no, "unmatched #ELSEIF"))?;
            let condition = parse_int(argument, line_no, "#ELSEIF")?;
            let val = random_value.unwrap();
            if last.1 {
                *last = (true, true);
            } else {
                let now = val == condition;
                *last = (!now, now);
            }
        } else if word == "ELSE" {
            let last = frames
                .last_mut()
                .ok_or_else(|| UrcError::new("syntax", line_no, "unmatched #ELSE"))?;
            *last = (last.1, true);
        } else if word == "ENDIF" {
            if frames.pop().is_none() {
                return Err(UrcError::new("syntax", line_no, "unmatched #ENDIF"));
            }
        } else if matches!(
            word.as_str(),
            "SETRANDOM" | "ENDRANDOM" | "SWITCH" | "CASE" | "SKIP" | "DEF" | "ENDSW" | "SETSWITCH"
        ) {
            return Err(UrcError::new(
                "unsupported-version",
                line_no,
                format!("unsupported BMS command: #{command}"),
            ));
        } else if frames.iter().any(|frame| frame.0) {
            continue;
        } else if word == "BPM" {
            let bpm = parse_float(argument, line_no, "#BPM")?;
            chart.bpm = Some(bpm);
        } else if command.is_ascii()
            && ((command.len() == 5 && word.starts_with("BPM"))
                || (command.len() == 7 && word.starts_with("EXBPM")))
        {
            let bpm = parse_float(argument, line_no, command)?;
            chart
                .bpm_defs
                .insert(norm_id(&command[command.len() - 2..], base), bpm);
        } else if command.is_ascii() && command.len() == 6 && word.starts_with("STOP") {
            let stop = parse_float(argument, line_no, command)?;
            chart
                .stop_defs
                .insert(norm_id(&command[command.len() - 2..], base), stop.abs() / 192.0);
        } else if command.is_ascii() && command.len() == 8 && word.starts_with("SCROLL") {
            let scroll = parse_float(argument, line_no, command)?;
            chart
                .scroll_defs
                .insert(norm_id(&command[command.len() - 2..], base), scroll);
        } else if word == "LNTYPE" {
            let lntype = parse_int(argument, line_no, "#LNTYPE")?;
            if lntype != 1 && lntype != 2 {
                return Err(UrcError::new(
                    "syntax",
                    line_no,
                    format!("unsupported #LNTYPE: {lntype}"),
                ));
            }
            chart.lntype = lntype as u32;
        } else if word == "LNOBJ" {
            chart.lnobj = if argument.is_empty() {
                None
            } else {
                Some(norm_id(argument, base))
            };
        } else if word == "TITLE" {
            chart.title = if argument.is_empty() {
                None
            } else {
                Some(argument.to_string())
            };
        } else if word == "ARTIST" {
            chart.artist = if argument.is_empty() {
                None
            } else {
                Some(argument.to_string())
            };
        } else if word == "PLAYLEVEL" {
            chart.play_level = if argument.is_empty() {
                None
            } else {
                Some(argument.to_string())
            };
        }
    }

    if !frames.is_empty() {
        return Err(UrcError::new("syntax", 1, "unterminated #IF block"));
    }

    Ok(chart)
}

fn decode_bytes(data: &[u8]) -> Result<String> {
    let bytes = if data.starts_with(b"\xef\xbb\xbf") {
        &data[3..]
    } else {
        data
    };

    let mut decoder_utf8 = encoding_rs::UTF_8.new_decoder_without_bom_handling();
    let mut utf8_str = String::with_capacity(bytes.len());
    let (_, _, malformed) = decoder_utf8.decode_to_string(bytes, &mut utf8_str, true);
    if !malformed {
        return Ok(utf8_str);
    }
    let mut decoder_sjis = encoding_rs::SHIFT_JIS.new_decoder_without_bom_handling();
    let mut sjis_str = String::with_capacity(bytes.len());
    let (_, _, malformed_sjis) = decoder_sjis.decode_to_string(bytes, &mut sjis_str, true);
    if !malformed_sjis {
        return Ok(sjis_str);
    }

    Err(UrcError::new(
        "syntax",
        1,
        "undecodable bytes: expected UTF-8 or Shift_JIS",
    ))
}

fn norm_id(token: &str, base: u32) -> String {
    if base == 36 {
        token.to_ascii_uppercase()
    } else {
        token.to_string()
    }
}

fn split_command(head: &str) -> (&str, &str) {
    match head.find([' ', '\t']) {
        Some(idx) => (&head[..idx], head[idx + 1..].trim()),
        None => (head, ""),
    }
}

fn parse_int(value: &str, line_no: u32, name: &str) -> Result<i64> {
    value
        .trim()
        .parse::<i64>()
        .map_err(|_| UrcError::new("syntax", line_no, format!("invalid {name}: {value}")))
}

fn parse_float(value: &str, line_no: u32, name: &str) -> Result<f64> {
    let parsed = value
        .trim()
        .parse::<f64>()
        .map_err(|_| UrcError::new("syntax", line_no, format!("invalid {name}: {value}")))?;
    if !parsed.is_finite() {
        return Err(UrcError::new(
            "syntax",
            line_no,
            format!("invalid {name}: {value}"),
        ));
    }
    Ok(parsed)
}

fn scan_base(text: &str) -> Result<u32> {
    for line in text.split('\n') {
        let parts: Vec<&str> = line.split_whitespace().collect();
        if parts.len() == 2 && parts[0].eq_ignore_ascii_case("#BASE") {
            let base = parts[1]
                .parse::<u32>()
                .map_err(|_| UrcError::new("syntax", 1, format!("invalid #BASE: {}", parts[1])))?;
            if base != 36 && base != 62 {
                return Err(UrcError::new(
                    "syntax",
                    1,
                    format!("unsupported #BASE: {base}"),
                ));
            }
            return Ok(base);
        }
    }
    Ok(36)
}

fn is_id_char(c: char) -> bool {
    c.is_ascii_alphanumeric()
}

fn is_relevant_channel(channel: &str) -> bool {
    if matches!(channel, "03" | "08" | "09" | "SC") {
        return true;
    }
    let bytes = channel.as_bytes();
    bytes.len() == 2
        && matches!(bytes[0], b'1' | b'2' | b'5' | b'6' | b'D' | b'E')
        && (b'1'..=b'9').contains(&bytes[1])
}

fn add_message(
    chart: &mut BmsChart,
    measure: i64,
    channel: &str,
    payload: &str,
    line_no: u32,
) -> Result<()> {
    if channel == "02" {
        let rate = parse_float(payload, line_no, "measure length")?;
        if rate <= 0.0 {
            return Err(UrcError::new(
                "syntax",
                line_no,
                format!("non-positive measure length: {payload}"),
            ));
        }
        chart.rates.insert(measure, rate);
        return Ok(());
    }

    if !payload.len().is_multiple_of(2) || !payload.chars().all(is_id_char) {
        return Err(UrcError::new(
            "syntax",
            line_no,
            format!("malformed object list: \"{payload}\""),
        ));
    }
    if payload.is_empty() || !is_relevant_channel(channel) {
        return Ok(());
    }

    let mut ids = Vec::with_capacity(payload.len() / 2);
    let bytes = payload.as_bytes();
    let uppercase = channel == "03" || chart.base == 36;
    for i in (0..bytes.len()).step_by(2) {
        let token = std::str::from_utf8(&bytes[i..i + 2]).unwrap();
        ids.push(if uppercase {
            token.to_ascii_uppercase()
        } else {
            token.to_string()
        });
    }

    chart
        .measures
        .entry(measure)
        .or_default()
        .entry(channel.to_string())
        .or_default()
        .push(ids);

    Ok(())
}
