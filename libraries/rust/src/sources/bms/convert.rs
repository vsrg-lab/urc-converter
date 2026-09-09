//! Mapper from the BMS-family source model onto a URC chart.

use std::collections::HashSet;

use crate::error::{Result, UrcError};
use crate::model::{Chart, Layout, Metadata, Note, NoteType, Version};
use crate::sources::shared::{build_timing, check_hold_overlap, round_ms};
use super::channels::{build_notes, channel_kind, detect_mode, id_value, resolve_layout, side_of};
use super::model::BmsChart;

const MEASURE_US: f64 = 240_000_000.0;

const SYSTEM_CHANNELS: [&str; 5] = ["02", "03", "08", "09", "SC"];

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
        Anchor,
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

    for y in &boundaries {
        entries.push((*y, 5, EntryKind::Anchor));
    }

    let mode = detect_mode(chart.pms, &used);

    let mut bpm: Option<f64> = None;
    let mut beats = 4_u64;
    let mut time_us = 0.0;
    let mut prev_y = 0.0;
    let mut pending_stop = 0.0;
    let mut timed = vec![0.0; objects.len()];
    let mut bpm_points: Vec<(i64, f64, u64, u64)> = Vec::new();
    let mut sv_points: Vec<(i64, f64)> = Vec::new();
    let mut anchors: Vec<i64> = Vec::new();

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
                EntryKind::Anchor => anchors.push(round_ms(time_us / 1000.0)),
            }
        }

        if new_bpm != bpm || new_beats != beats {
            bpm_points.push((round_ms(time_us / 1000.0), new_bpm.unwrap(), new_beats, 4));
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

    let anchor_ms = anchors
        .iter()
        .find(|time| **time >= first_note_time)
        .copied();

    let timing = build_timing(&bpm_points, &sv_points, first_note_time, ".bms", anchor_ms)?;

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

    let (keys, special_keys, special_lanes) = resolve_layout(mode);

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



