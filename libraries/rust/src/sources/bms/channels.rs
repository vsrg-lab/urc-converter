use std::collections::HashSet;

pub fn id_value(text: &str, base: u32) -> u64 {
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

pub fn channel_kind(channel: &str) -> Option<&'static str> {
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

pub fn side_of(c: char) -> usize {
    match c {
        '1' | '5' | 'D' => 0,
        _ => 1,
    }
}

pub fn detect_mode(pms: bool, used: &HashSet<(usize, char)>) -> &'static str {
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

pub fn get_lane(mode: &'static str, channel: &str) -> Option<u32> {
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

pub fn resolve_layout(mode: &'static str) -> (u64, u64, Option<Vec<u32>>) {
    match mode {
        "5K" => (5, 1, Some(vec![0])),
        "7K" => (7, 1, Some(vec![0])),
        "10K" => (10, 2, Some(vec![0, 6])),
        "14K" => (14, 2, Some(vec![0, 8])),
        "PMS9" => (9, 0, None),
        "PMS18" => (18, 0, None),
        _ => unreachable!(),
    }
}

pub fn pair_long_notes(
    chart: &super::model::BmsChart,
    stream: &[(f64, String)],
    lane: u32,
    notes: &mut Vec<(i64, u32, crate::model::NoteType)>,
) -> crate::error::Result<()> {
    let mut start: Option<f64> = None;
    if chart.lntype == 1 {
        for (time, obj) in stream {
            if obj == "00" {
                continue;
            }
            match start {
                None => start = Some(*time),
                Some(s) => {
                    notes.push((crate::sources::shared::round_ms(s / 1000.0), lane, crate::model::NoteType::Ls));
                    notes.push((crate::sources::shared::round_ms(time / 1000.0), lane, crate::model::NoteType::Le));
                    start = None;
                }
            }
        }
    } else {
        for (time, obj) in stream {
            if obj == "00" {
                if let Some(s) = start {
                    notes.push((crate::sources::shared::round_ms(s / 1000.0), lane, crate::model::NoteType::Ls));
                    notes.push((crate::sources::shared::round_ms(time / 1000.0), lane, crate::model::NoteType::Le));
                    start = None;
                }
            } else if start.is_none() {
                start = Some(*time);
            }
        }
    }

    if start.is_some() {
        return Err(crate::error::UrcError::new(
            "syntax",
            1,
            format!("long note on lane {lane} has no end"),
        ));
    }

    Ok(())
}

pub fn build_notes(
    chart: &super::model::BmsChart,
    mode: &'static str,
    objects: &[(f64, String, String)],
    timed: &[f64],
) -> crate::error::Result<Vec<(i64, u32, crate::model::NoteType)>> {
    let mut streams: std::collections::BTreeMap<String, Vec<(f64, String)>> = std::collections::BTreeMap::new();
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
                    notes.push((crate::sources::shared::round_ms(time / 1000.0), lane, crate::model::NoteType::M));
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
                        notes.push((crate::sources::shared::round_ms(p / 1000.0), lane, crate::model::NoteType::Ls));
                        notes.push((crate::sources::shared::round_ms(time / 1000.0), lane, crate::model::NoteType::Le));
                        pending = None;
                    } else {
                        if let Some(p) = pending {
                            notes.push((crate::sources::shared::round_ms(p / 1000.0), lane, crate::model::NoteType::N));
                        }
                        pending = if obj != "00" { Some(*time) } else { None };
                    }
                }
                if let Some(p) = pending {
                    notes.push((crate::sources::shared::round_ms(p / 1000.0), lane, crate::model::NoteType::N));
                }
            }
        }
    }

    Ok(notes)
}
