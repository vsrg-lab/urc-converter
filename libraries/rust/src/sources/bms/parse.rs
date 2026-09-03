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

struct JavaRandom {
    state: u64,
}

impl JavaRandom {
    const MASK: u64 = (1_u64 << 48) - 1;
    const MULT: u64 = 0x5DEECE66D;
    const ADD: u64 = 0xB;

    fn new(seed: i64) -> Self {
        Self {
            state: ((seed as u64) ^ Self::MULT) & Self::MASK,
        }
    }

    fn next(&mut self, bits: u32) -> u64 {
        self.state = (self.state.wrapping_mul(Self::MULT).wrapping_add(Self::ADD)) & Self::MASK;
        self.state >> (48 - bits)
    }

    fn next_int(&mut self, bound: u64) -> u64 {
        if bound & bound.wrapping_neg() == bound {
            return (bound.wrapping_mul(self.next(31))) >> 31;
        }
        loop {
            let bits = self.next(31);
            let val = bits % bound;
            if bits.wrapping_sub(val).wrapping_add(bound - 1) <= 0x7FFF_FFFF {
                return val;
            }
        }
    }
}

/// Parses BMS-family bytes into a source model.
pub fn parse_bms(data: &[u8], options: &BmsParseOptions) -> Result<BmsChart> {
    let text = decode_bytes(data)?;
    let base = scan_base(&text)?;

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
    let mut random_value: Option<u64> = None;
    let mut branch_index = 0;

    for (offset, raw) in text
        .strip_prefix('\u{feff}')
        .unwrap_or(&text)
        .lines()
        .enumerate()
    {
        let line_no = offset as u32 + 1;
        let line = raw.trim();
        if !line.starts_with('#') {
            continue;
        }
        let head = &line[1..];

        if head.len() >= 6 && head[..3].chars().all(|c| c.is_ascii_digit()) && &head[5..6] == ":" {
            if !frames.iter().any(|frame| frame.0) {
                let measure = head[..3].parse::<i64>().unwrap();
                let channel = &head[3..5];
                let payload = &head[6..];
                add_message(&mut chart, measure, channel, payload, line_no)?;
            }
            continue;
        }

        let (command, argument) = match head.find(' ') {
            Some(idx) => (&head[..idx], head[idx + 1..].trim()),
            None => (head, ""),
        };
        let word = command.to_ascii_uppercase();

        if word == "RANDOM" {
            let count = argument.parse::<u64>().map_err(|_| {
                UrcError::new("syntax", line_no, format!("invalid #RANDOM: {argument}"))
            })?;
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
                if !(1..=count).contains(&p) {
                    return Err(UrcError::new(
                        "syntax",
                        line_no,
                        format!("branch pick out of range: {p}"),
                    ));
                }
                p
            } else if let Some(ref mut rng) = random_gen {
                rng.next_int(count) + 1
            } else {
                1
            };

            random_value = Some(pick);
            branch_index += 1;
        } else if word == "IF" {
            let val =
                random_value.ok_or_else(|| UrcError::new("syntax", line_no, "unmatched #IF"))?;
            let condition = argument.parse::<u64>().map_err(|_| {
                UrcError::new("syntax", line_no, format!("invalid #IF: {argument}"))
            })?;
            frames.push((val != condition, val == condition));
        } else if word == "ELSEIF" {
            let last = frames
                .last_mut()
                .ok_or_else(|| UrcError::new("syntax", line_no, "unmatched #ELSEIF"))?;
            let condition = argument.parse::<u64>().map_err(|_| {
                UrcError::new("syntax", line_no, format!("invalid #ELSEIF: {argument}"))
            })?;
            let val = random_value.unwrap();
            let matched = last.1 || val == condition;
            *last = (!matched, matched);
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
            let bpm = argument.parse::<f64>().map_err(|_| {
                UrcError::new("syntax", line_no, format!("invalid #BPM: {argument}"))
            })?;
            chart.bpm = Some(bpm);
        } else if (command.len() == 5 && command[..3].eq_ignore_ascii_case("BPM"))
            || (command.len() == 8 && command[..6].eq_ignore_ascii_case("EXBPM"))
        {
            let bpm = argument.parse::<f64>().map_err(|_| {
                UrcError::new("syntax", line_no, format!("invalid {command}: {argument}"))
            })?;
            chart
                .bpm_defs
                .insert(command[command.len() - 2..].to_string(), bpm);
        } else if command.len() == 6 && command[..4].eq_ignore_ascii_case("STOP") {
            let stop = argument.parse::<f64>().map_err(|_| {
                UrcError::new("syntax", line_no, format!("invalid {command}: {argument}"))
            })?;
            chart
                .stop_defs
                .insert(command[command.len() - 2..].to_string(), stop.abs() / 192.0);
        } else if command.len() == 8 && command[..6].eq_ignore_ascii_case("SCROLL") {
            let scroll = argument.parse::<f64>().map_err(|_| {
                UrcError::new("syntax", line_no, format!("invalid {command}: {argument}"))
            })?;
            chart
                .scroll_defs
                .insert(command[command.len() - 2..].to_string(), scroll);
        } else if word == "LNTYPE" {
            let lntype = argument.parse::<u32>().map_err(|_| {
                UrcError::new("syntax", line_no, format!("invalid #LNTYPE: {argument}"))
            })?;
            if lntype != 1 && lntype != 2 {
                return Err(UrcError::new(
                    "syntax",
                    line_no,
                    format!("unsupported #LNTYPE: {lntype}"),
                ));
            }
            chart.lntype = lntype;
        } else if word == "LNOBJ" {
            chart.lnobj = if argument.is_empty() {
                None
            } else {
                Some(argument.to_string())
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

fn scan_base(text: &str) -> Result<u32> {
    for line in text.lines() {
        let parts: Vec<&str> = line.trim().split_whitespace().collect();
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

fn add_message(
    chart: &mut BmsChart,
    measure: i64,
    channel: &str,
    payload: &str,
    line_no: u32,
) -> Result<()> {
    if channel == "02" {
        let rate = payload.trim().parse::<f64>().map_err(|_| {
            UrcError::new(
                "syntax",
                line_no,
                format!("invalid measure length: {payload}"),
            )
        })?;
        if rate < 0.0 {
            return Err(UrcError::new(
                "syntax",
                line_no,
                format!("negative measure length: {payload}"),
            ));
        }
        chart.rates.insert(measure, rate);
        return Ok(());
    }

    if payload.len() % 2 != 0 || !payload.chars().all(is_id_char) {
        return Err(UrcError::new(
            "syntax",
            line_no,
            format!("malformed object list: \"{payload}\""),
        ));
    }

    let mut ids = Vec::new();
    let bytes = payload.as_bytes();
    for i in (0..bytes.len()).step_by(2) {
        ids.push(std::str::from_utf8(&bytes[i..i + 2]).unwrap().to_string());
    }

    chart
        .measures
        .entry(measure)
        .or_default()
        .entry(channel.to_string())
        .or_default()
        .extend(ids);

    Ok(())
}
