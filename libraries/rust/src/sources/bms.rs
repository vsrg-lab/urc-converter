//! BMS-family source parser and converter.

use std::collections::{BTreeMap, HashSet};

use super::shared::{build_timing, check_hold_overlap, round_ms};
use crate::error::{Result, UrcError};
use crate::model::{Chart, Layout, Metadata, Note, NoteType, Version};

const MEASURE_US: f64 = 240_000_000.0;

const SYSTEM_CHANNELS: [&str; 5] = ["02", "03", "08", "09", "SC"];

/// Options for BMS parsing and random branch resolution.
#[derive(Debug, Clone, Default)]
pub struct BmsParseOptions<'a> {
    pub pms: bool,
    pub seed: Option<i64>,
    pub branches: Option<&'a [u64]>,
}

/// Source model of a BMS-family chart.
#[derive(Debug, Clone)]
pub struct BmsChart {
    pub pms: bool,
    pub base: u32,
    pub title: Option<String>,
    pub artist: Option<String>,
    pub play_level: Option<String>,
    pub bpm: Option<f64>,
    pub lntype: u32,
    pub lnobj: Option<String>,
    pub bpm_defs: BTreeMap<String, f64>,
    pub stop_defs: BTreeMap<String, f64>,
    pub scroll_defs: BTreeMap<String, f64>,
    pub rates: BTreeMap<i64, f64>,
    pub measures: BTreeMap<i64, BTreeMap<String, Vec<String>>>,
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

fn id_value(text: &str, base: u32) -> u64 {
    fn digit(c: char) -> u64 {
        if c.is_ascii_digit() {
            (c as u64) - ('0' as u64)
        } else if c.is_ascii_uppercase() {
            (c as u64) - ('A' as u64) + 10
        } else {
            (c as u64) - ('a' as u64) + 36
        }
    }
    let chars: Vec<char> = text.chars().collect();
    digit(chars[0]) * (base as u64) + digit(chars[1])
}

fn channel_kind(channel: &str) -> Option<&'static str> {
    if channel.len() != 2 {
        return None;
    }
    let mut chars = channel.chars();
    let first = chars.next()?;
    let second = chars.next()?;
    if (first == '1' || first == '2') && ('1'..='9').contains(&second) {
        Some("visible")
    } else if (first == '5' || first == '6') && ('1'..='9').contains(&second) {
        Some("ln")
    } else if (first == 'D' || first == 'E') && ('1'..='9').contains(&second) {
        Some("mine")
    } else {
        None
    }
}

fn side_of(c: char) -> usize {
    match c {
        '1' | '5' | 'D' => 0,
        _ => 1,
    }
}

fn detect_mode(pms: bool, used: &HashSet<(usize, char)>) -> &'static str {
    if pms {
        for &(side, second) in used {
            if matches!(second, '6' | '7' | '8' | '9') || (side == 1 && second == '1') {
                return "PMS18";
            }
        }
        return "PMS9";
    }

    let mut seven = false;
    let mut double = false;
    for &(side, second) in used {
        if matches!(second, '8' | '9') {
            seven = true;
        }
        if side == 1 {
            double = true;
        }
    }

    if seven && double {
        "14K"
    } else if double {
        "10K"
    } else if seven {
        "7K"
    } else {
        "5K"
    }
}

fn get_lane(mode: &'static str, channel: &str) -> Option<u32> {
    let side = side_of(channel.chars().next()?);
    let key = channel.chars().nth(1)?;
    match (mode, side) {
        ("5K" | "10K", side) => match key {
            '6' => Some(side as u32 * 6),
            '1'..='5' => Some((key as u32 - '0' as u32) + side as u32 * 6),
            _ => None,
        },
        ("7K" | "14K", side) => match key {
            '6' => Some(side as u32 * 8),
            '1'..='5' => Some((key as u32 - '0' as u32) + side as u32 * 8),
            '8'..='9' => Some((key as u32 - '8' as u32 + 6) + side as u32 * 8),
            _ => None,
        },
        ("PMS9", 0) => match key {
            '1'..='5' => Some(key as u32 - '1' as u32),
            _ => None,
        },
        ("PMS9", 1) => match key {
            '2'..='5' => Some(key as u32 - '2' as u32 + 5),
            _ => None,
        },
        ("PMS18", side) => {
            let base = side as u32 * 9;
            let offset = match key {
                '1'..='5' => key as u32 - '1' as u32,
                '8' => 5,
                '9' => 6,
                '6' => 7,
                '7' => 8,
                _ => return None,
            };
            Some(base + offset)
        }
        _ => None,
    }
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

fn pair_long_notes(
    chart: &BmsChart,
    stream: &[(f64, String)],
    lane: u32,
    notes: &mut Vec<(i64, u32, NoteType)>,
) -> Result<()> {
    let mut start: Option<f64> = None;
    if chart.lntype == 1 {
        for (time, obj) in stream {
            if obj == "00" {
                continue;
            }
            match start {
                None => start = Some(*time),
                Some(s) => {
                    notes.push((round_ms(s / 1000.0), lane, NoteType::Ls));
                    notes.push((round_ms(time / 1000.0), lane, NoteType::Le));
                    start = None;
                }
            }
        }
    } else {
        for (time, obj) in stream {
            if obj == "00" {
                if let Some(s) = start {
                    notes.push((round_ms(s / 1000.0), lane, NoteType::Ls));
                    notes.push((round_ms(time / 1000.0), lane, NoteType::Le));
                    start = None;
                }
            } else if start.is_none() {
                start = Some(*time);
            }
        }
    }

    if start.is_some() {
        return Err(UrcError::new(
            "syntax",
            1,
            format!("long note on lane {lane} has no end"),
        ));
    }

    Ok(())
}

fn build_notes(
    chart: &BmsChart,
    mode: &'static str,
    objects: &[(f64, String, String)],
    timed: &[f64],
) -> Result<Vec<(i64, u32, NoteType)>> {
    let mut streams: BTreeMap<String, Vec<(f64, String)>> = BTreeMap::new();
    for (index, (_y, channel, obj)) in objects.iter().enumerate() {
        streams
            .entry(channel.clone())
            .or_default()
            .push((timed[index], obj.clone()));
    }

    let mut notes = Vec::new();
    for (channel, stream) in &streams {
        let lane = match get_lane(mode, channel) {
            Some(l) => l,
            None => continue,
        };
        let kind = match channel_kind(channel) {
            Some(k) => k,
            None => continue,
        };

        match kind {
            "mine" => {
                for (time, _) in stream {
                    notes.push((round_ms(time / 1000.0), lane, NoteType::M));
                }
            }
            "ln" => {
                pair_long_notes(chart, stream, lane, &mut notes)?;
            }
            _ => {
                let mut pending: Option<f64> = None;
                for (time, obj) in stream {
                    if let Some(ref lnobj) = chart.lnobj
                        && obj == lnobj
                        && let Some(p) = pending
                    {
                        notes.push((round_ms(p / 1000.0), lane, NoteType::Ls));
                        notes.push((round_ms(time / 1000.0), lane, NoteType::Le));
                        pending = None;
                    } else {
                        if let Some(p) = pending {
                            notes.push((round_ms(p / 1000.0), lane, NoteType::N));
                        }
                        pending = Some(*time);
                    }
                }
                if let Some(p) = pending {
                    notes.push((round_ms(p / 1000.0), lane, NoteType::N));
                }
            }
        }
    }

    Ok(notes)
}

/// Maps a BMS-family chart onto a URC chart.
pub fn convert_bms(chart: &BmsChart) -> Result<Chart> {
    let bpm_initial = chart
        .bpm
        .ok_or_else(|| UrcError::new("syntax", 1, "missing or non-positive #BPM"))?;
    if bpm_initial <= 0.0 {
        return Err(UrcError::new("syntax", 1, "missing or non-positive #BPM"));
    }

    let max_measure = chart.measures.keys().copied().max().unwrap_or(-1);
    let mut boundaries = vec![0.0];
    for m in 0..=max_measure {
        let rate = chart.rates.get(&m).copied().unwrap_or(1.0);
        boundaries.push(boundaries.last().unwrap() + rate);
    }

    #[derive(Clone)]
    enum EntryKind {
        Bpm(f64),
        Meter(u64),
        Stop(f64),
        Scroll(f64),
        Object(usize),
    }

    let mut entries: Vec<(f64, usize, EntryKind)> = vec![(0.0, 0, EntryKind::Bpm(bpm_initial))];
    let mut objects: Vec<(f64, String, String)> = Vec::new();
    let mut used = HashSet::new();

    for m in 0..=max_measure {
        let rate = chart.rates.get(&m).copied().unwrap_or(1.0);
        let prev_rate = chart.rates.get(&(m - 1)).copied().unwrap_or(1.0);
        if rate != prev_rate {
            let beats = rate * 4.0;
            if (beats - beats.round()).abs() < 1e-9 && beats.round() >= 1.0 {
                entries.push((
                    boundaries[m as usize],
                    3,
                    EntryKind::Meter(beats.round() as u64),
                ));
            }
        }

        if let Some(measure_map) = chart.measures.get(&m) {
            for (channel, ids) in measure_map {
                for (idx, obj) in ids.iter().enumerate() {
                    let y = boundaries[m as usize] + (idx as f64 / ids.len() as f64) * rate;

                    if SYSTEM_CHANNELS.contains(&channel.as_str()) {
                        if obj == "00" {
                            continue;
                        }
                        if channel == "03" {
                            let digits = id_value(obj, chart.base);
                            let bpm_val = ((digits / 36) * 16 + (digits % 36)) as f64;
                            entries.push((y, 0, EntryKind::Bpm(bpm_val)));
                        } else if channel == "08" {
                            let bpm_val = chart.bpm_defs.get(obj).copied().ok_or_else(|| {
                                UrcError::new("syntax", 1, format!("undefined #BPM{obj}"))
                            })?;
                            entries.push((y, 0, EntryKind::Bpm(bpm_val)));
                        } else if channel == "09" {
                            let stop_val = chart.stop_defs.get(obj).copied().ok_or_else(|| {
                                UrcError::new("syntax", 1, format!("undefined #STOP{obj}"))
                            })?;
                            entries.push((y, 1, EntryKind::Stop(stop_val)));
                        } else {
                            let scroll_val =
                                chart.scroll_defs.get(obj).copied().ok_or_else(|| {
                                    UrcError::new("syntax", 1, format!("undefined #SCROLL{obj}"))
                                })?;
                            entries.push((y, 2, EntryKind::Scroll(scroll_val)));
                        }
                        continue;
                    }

                    let kind = match channel_kind(channel) {
                        Some(k) => k,
                        None => continue,
                    };
                    if obj != "00" {
                        let side = side_of(channel.chars().next().unwrap());
                        let second = channel.chars().nth(1).unwrap();
                        used.insert((side, second));
                    }
                    if obj == "00" && kind != "ln" {
                        continue;
                    }

                    objects.push((y, channel.clone(), obj.clone()));
                    entries.push((y, 4, EntryKind::Object(objects.len() - 1)));
                }
            }
        }
    }

    let mode = detect_mode(chart.pms, &used);

    let mut bpm: Option<f64> = None;
    let mut beats = 4_u64;
    let mut time_us = 0.0;
    let mut prev_y = 0.0;
    let mut pending_stop = 0.0;
    let mut timed = vec![0.0; objects.len()];
    let mut bpm_points: Vec<(i64, f64, u64)> = Vec::new();
    let mut sv_points: Vec<(i64, f64)> = Vec::new();

    entries.sort_by(|a, b| {
        a.0.partial_cmp(&b.0)
            .unwrap_or(std::cmp::Ordering::Equal)
            .then_with(|| a.1.cmp(&b.1))
    });

    let mut i = 0;
    while i < entries.len() {
        let y = entries[i].0;
        let mut group = Vec::new();
        while i < entries.len() && entries[i].0 == y {
            group.push(entries[i].clone());
            i += 1;
        }

        if let Some(b) = bpm {
            time_us += MEASURE_US * (y - prev_y) / b;
        }
        time_us += pending_stop;
        pending_stop = 0.0;

        let mut new_bpm = bpm;
        let mut new_beats = beats;
        let mut scroll: Option<f64> = None;

        for (_, _, kind) in group {
            match kind {
                EntryKind::Bpm(v) => new_bpm = Some(v),
                EntryKind::Meter(v) => new_beats = v,
                EntryKind::Stop(v) => pending_stop = MEASURE_US * v / new_bpm.unwrap(),
                EntryKind::Scroll(v) => scroll = Some(v),
                EntryKind::Object(idx) => timed[idx] = time_us,
            }
        }

        if new_bpm != bpm || new_beats != beats {
            bpm_points.push((round_ms(time_us / 1000.0), new_bpm.unwrap(), new_beats));
        }
        if let Some(s) = scroll {
            sv_points.push((round_ms(time_us / 1000.0), s));
        }

        bpm = new_bpm;
        beats = new_beats;
        prev_y = y;
    }

    let raw_notes = build_notes(chart, mode, &objects, &timed)?;
    let first_note_time = raw_notes
        .iter()
        .filter(|(_, _, t)| *t != NoteType::Le)
        .map(|(time, _, _)| *time)
        .min()
        .unwrap_or(0);

    let timing = build_timing(&bpm_points, &sv_points, first_note_time, ".bms")?;

    let mut urc_notes: Vec<Note> = raw_notes
        .into_iter()
        .map(|(t, lane, note_type)| Note {
            timestamp_ms: t - first_note_time,
            lane,
            note_type,
        })
        .collect();

    fn type_order(nt: NoteType) -> usize {
        match nt {
            NoteType::N => 0,
            NoteType::Ls => 1,
            NoteType::Le => 2,
            NoteType::M => 3,
            NoteType::F => 4,
        }
    }

    urc_notes.sort_by(|a, b| {
        a.timestamp_ms
            .cmp(&b.timestamp_ms)
            .then_with(|| a.lane.cmp(&b.lane))
            .then_with(|| type_order(a.note_type).cmp(&type_order(b.note_type)))
    });

    check_hold_overlap(&urc_notes)?;

    let (keys, special_keys, special_lanes) = match mode {
        "5K" => (5, 1, Some(vec![0])),
        "7K" => (7, 1, Some(vec![0])),
        "10K" => (10, 2, Some(vec![0, 6])),
        "14K" => (14, 2, Some(vec![0, 8])),
        "PMS9" => (9, 0, None),
        "PMS18" => (18, 0, None),
        _ => unreachable!(),
    };

    Ok(Chart {
        format_version: Version { major: 1, minor: 1 },
        metadata: Metadata {
            original: if chart.pms {
                "PMS".to_string()
            } else {
                "BMS".to_string()
            },
            title: chart.title.clone().unwrap_or_else(|| "Unknown".to_string()),
            artist: chart
                .artist
                .clone()
                .unwrap_or_else(|| "Unknown".to_string()),
            creator: "Unknown".to_string(),
            version: chart
                .play_level
                .clone()
                .unwrap_or_else(|| "Unknown".to_string()),
        },
        judgment: None,
        layout: Layout {
            keys,
            special_keys,
            special_lanes,
        },
        timing,
        notes: urc_notes,
    })
}
