//! Mapper from the StepMania source model onto URC charts.

use crate::error::{Result, UrcError};
use crate::model::{Chart, Layout, Metadata, Note, NoteType, Version};

use super::super::shared::{build_timing, check_hold_overlap, round_ms};
use super::model::{NoteKind, SmChart, SmFile, SmNote, Timing};
use super::parse::resolve_lanes;

const MEASURE_ROWS: i64 = 192;
const FAST_BPM_WARP: f64 = 9999999.0;
const ROLL_TAP_SPACING_MS: i64 = 500;

type BpmPoint = (i64, f64, u64, u64);

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum EntryKind {
    Warp,
    Delay,
    Note,
    Tail,
    Anchor,
    Stop,
    Bpm,
    TimeSignature,
    Scroll,
}

#[derive(Debug, Clone, Copy)]
enum EntryValue {
    Number(f64),
    NoteIndex(usize),
    Meter(u64, u64),
}

/// Converts every chart of a simfile into URC charts.
pub fn convert_sm(simfile: &SmFile) -> Result<Vec<Chart>> {
    simfile.charts.iter().map(|chart| convert_chart(simfile, chart)).collect()
}

fn convert_chart(simfile: &SmFile, chart: &SmChart) -> Result<Chart> {
    let lanes = resolve_lanes(&chart.steps_type)?;
    let timing = chart.timing.as_ref().unwrap_or(&simfile.timing);
    let (offset, bpm_segs, stop_segs, warp_segs) = preprocess(timing)?;
    let mut all_warps = warp_segs;
    all_warps.extend_from_slice(&timing.warps);
    let intervals = warp_intervals(&all_warps);
    let mut notes = chart.notes.clone();
    filter_notes(&mut notes, &intervals);

    let mut entries: Vec<(i64, u8, EntryKind, EntryValue)> = Vec::new();
    for &(start, dest) in &intervals {
        entries.push((start, 0, EntryKind::Warp, EntryValue::Number(dest as f64)));
    }
    for &(beat, seconds) in &timing.delays {
        entries.push((rows(beat), 1, EntryKind::Delay, EntryValue::Number(seconds)));
    }
    for (index, note) in notes.iter().enumerate() {
        entries.push((note.row, 2, EntryKind::Note, EntryValue::NoteIndex(index)));
        if let Some(tail_row) = note.tail_row {
            entries.push((tail_row, 2, EntryKind::Tail, EntryValue::NoteIndex(index)));
        }
    }
    for &(beat, seconds) in &stop_segs {
        entries.push((rows(beat), 3, EntryKind::Stop, EntryValue::Number(seconds)));
    }
    for &(beat, bpm) in &bpm_segs {
        entries.push((rows(beat), 4, EntryKind::Bpm, EntryValue::Number(bpm)));
    }
    for &(beat, numerator, denominator) in &timing.timesigs {
        entries.push((
            rows(beat),
            5,
            EntryKind::TimeSignature,
            EntryValue::Meter(numerator, denominator),
        ));
    }
    for &(beat, ratio) in &timing.scrolls {
        entries.push((rows(beat), 6, EntryKind::Scroll, EntryValue::Number(ratio)));
    }
    let max_row = entries.iter().map(|entry| entry.0).max().unwrap_or(0);
    let mut row = 0;
    while row < max_row + MEASURE_ROWS {
        entries.push((row, 2, EntryKind::Anchor, EntryValue::Number(0.0)));
        row += MEASURE_ROWS;
    }

    let mut head_times = vec![0.0_f64; notes.len()];
    let mut tail_times = vec![0.0_f64; notes.len()];
    let mut bpm_points: Vec<BpmPoint> = Vec::new();
    let mut sv_points: Vec<(i64, f64)> = Vec::new();
    let mut anchors: Vec<i64> = Vec::new();

    let mut seconds = -offset;
    let mut bpm: Option<f64> = None;
    let mut meter = (4_u64, 4_u64);
    let mut multiplier = 1.0_f64;
    let mut warping = false;
    let mut warp_dest = 0_i64;
    let mut prev_row: Option<i64> = None;

    entries.sort_by_key(|entry| (entry.0, entry.1));
    let mut i = 0;
    while i < entries.len() {
        let row = entries[i].0;
        if let Some(prev) = prev_row
            && !warping
            && let Some(current) = bpm
        {
            seconds += (row - prev) as f64 / 48.0 * 60.0 / current;
        }
        prev_row = Some(row);
        if warping && row >= warp_dest {
            warping = false;
        }

        let mut new_bpm = bpm;
        let mut new_meter = meter;
        let mut new_multiplier = multiplier;
        while i < entries.len() && entries[i].0 == row {
            let (_, _, kind, value) = &entries[i];
            match (kind, value) {
                (EntryKind::Warp, EntryValue::Number(dest)) => {
                    let dest = *dest as i64;
                    if warping {
                        warp_dest = warp_dest.max(dest);
                    } else {
                        warping = true;
                        warp_dest = dest;
                    }
                }
                (EntryKind::Delay, EntryValue::Number(pause)) => seconds += *pause,
                (EntryKind::Note, EntryValue::NoteIndex(index)) => {
                    head_times[*index] = seconds;
                }
                (EntryKind::Tail, EntryValue::NoteIndex(index)) => {
                    tail_times[*index] = seconds;
                }
                (EntryKind::Anchor, _) => anchors.push(round_ms(seconds * 1000.0)),
                (EntryKind::Stop, EntryValue::Number(pause)) => seconds += *pause,
                (EntryKind::Bpm, EntryValue::Number(value)) => new_bpm = Some(*value),
                (EntryKind::TimeSignature, EntryValue::Meter(num, den)) => {
                    new_meter = (*num, *den);
                }
                (EntryKind::Scroll, EntryValue::Number(ratio)) => new_multiplier = *ratio,
                _ => unreachable!("entry kind/value mismatch"),
            }
            i += 1;
        }

        if let Some(emitted_bpm) = new_bpm
            && (new_bpm != bpm || new_meter != meter)
        {
            bpm_points.push((round_ms(seconds * 1000.0), emitted_bpm, new_meter.0, new_meter.1));
        }
        if new_multiplier != multiplier {
            sv_points.push((round_ms(seconds * 1000.0), new_multiplier));
        }
        bpm = new_bpm;
        meter = new_meter;
        multiplier = new_multiplier;
    }

    let urc_notes = build_urc_notes(timing, &notes, &head_times, &tail_times)?;
    let first_note_time = urc_notes
        .iter()
        .filter(|note| note.2 != NoteType::Le)
        .map(|note| note.0)
        .min()
        .unwrap_or(0);

    let timing_points = build_timing(
        &bpm_points,
        &sv_points,
        first_note_time,
        ".sm",
        anchors.iter().copied().find(|time| *time >= first_note_time),
    )?;

    let type_order = |kind: NoteType| match kind {
        NoteType::N => 0,
        NoteType::Ls => 1,
        NoteType::Le => 2,
        NoteType::M => 3,
        NoteType::F => 4,
    };
    let mut ordered: Vec<(i64, u32, NoteType)> = urc_notes;
    ordered.sort_by_key(|note| (note.0, note.1, type_order(note.2)));
    let final_notes: Vec<Note> = ordered
        .into_iter()
        .map(|(time, lane, note_type)| Note {
            timestamp_ms: time - first_note_time,
            lane,
            note_type,
        })
        .collect();
    check_hold_overlap(&final_notes)?;

    let title = [simfile.title.as_str(), simfile.subtitle.as_str()]
        .iter()
        .filter(|part| !part.is_empty())
        .cloned()
        .collect::<Vec<&str>>()
        .join(" ");
    Ok(Chart {
        format_version: Version { major: 1, minor: 1 },
        metadata: Metadata {
            original: "StepMania".to_string(),
            title: if title.is_empty() { "Unknown".to_string() } else { title },
            artist: if simfile.artist.is_empty() {
                "Unknown".to_string()
            } else {
                simfile.artist.clone()
            },
            creator: first_non_empty(&chart.credit, &simfile.credit, "Unknown").to_string(),
            version: if chart.chartname.is_empty() {
                difficulty_name(&chart.difficulty, &chart.description).to_string()
            } else {
                chart.chartname.clone()
            },
        },
        judgment: None,
        layout: Layout {
            keys: lanes as u64,
            special_keys: 0,
            special_lanes: None,
        },
        timing: timing_points,
        notes: final_notes,
    })
}

type UrcNote = (i64, u32, NoteType);

fn build_urc_notes(
    timing: &Timing,
    notes: &[SmNote],
    head_times: &[f64],
    tail_times: &[f64],
) -> Result<Vec<UrcNote>> {
    let fake_ranges: Vec<(i64, i64)> = timing
        .fakes
        .iter()
        .map(|&(beat, length)| (rows(beat), rows(beat) + rows(length)))
        .collect();
    let mut urc_notes: Vec<UrcNote> = Vec::new();

    for (index, note) in notes.iter().enumerate() {
        let head_ms = round_ms(head_times[index] * 1000.0);
        if fake_ranges.iter().any(|&(start, end)| start <= note.row && note.row < end) {
            urc_notes.push((head_ms, note.track, NoteType::F));
            continue;
        }
        match note.kind {
            NoteKind::Hold => {
                let tail_ms = round_ms(tail_times[index] * 1000.0);
                if tail_ms <= head_ms {
                    return Err(UrcError::new(
                        "syntax",
                        1,
                        format!("hold on lane {} collapses to zero length", note.track),
                    ));
                }
                urc_notes.push((head_ms, note.track, NoteType::Ls));
                urc_notes.push((tail_ms, note.track, NoteType::Le));
            }
            NoteKind::Roll => {
                let end_ms = round_ms(tail_times[index] * 1000.0);
                urc_notes.push((head_ms, note.track, NoteType::N));
                let mut tap_ms = head_ms + ROLL_TAP_SPACING_MS;
                while tap_ms < end_ms {
                    urc_notes.push((tap_ms, note.track, NoteType::N));
                    tap_ms += ROLL_TAP_SPACING_MS;
                }
            }
            NoteKind::Mine => urc_notes.push((head_ms, note.track, NoteType::M)),
            NoteKind::Fake => urc_notes.push((head_ms, note.track, NoteType::F)),
            NoteKind::Tap | NoteKind::Lift => urc_notes.push((head_ms, note.track, NoteType::N)),
        }
    }
    Ok(urc_notes)
}

fn filter_notes(notes: &mut Vec<SmNote>, intervals: &[(i64, i64)]) {
    notes.retain(|note| !intervals.iter().any(|&(start, dest)| start < note.row && note.row < dest));
    for note in notes.iter_mut() {
        if let Some(tail_row) = note.tail_row
            && let Some(&(start, _)) = intervals
                .iter()
                .find(|&&(start, dest)| start < tail_row && tail_row < dest)
        {
            note.tail_row = Some(start);
        }
    }
}

fn warp_intervals(warp_segs: &[(f64, f64)]) -> Vec<(i64, i64)> {
    let mut spans: Vec<(i64, i64)> = warp_segs
        .iter()
        .map(|&(beat, length)| (rows(beat), rows(beat) + rows(length)))
        .collect();
    spans.sort_by_key(|span| span.0);
    let mut merged: Vec<(i64, i64)> = Vec::new();
    for (start, dest) in spans {
        match merged.last_mut() {
            Some(last) if start < last.1 => last.1 = last.1.max(dest),
            _ => merged.push((start, dest)),
        }
    }
    merged
}

type Segments = (f64, Vec<(f64, f64)>, Vec<(f64, f64)>, Vec<(f64, f64)>);

/// Port of `SMLoader::ProcessBPMsAndStops`: normalizes negative BPMs/stops
/// into warps.
fn preprocess(timing: &Timing) -> Result<Segments> {
    let mut bpms = timing.bpms.clone();
    bpms.sort_by(|left, right| left.0.partial_cmp(&right.0).unwrap());
    let mut sorted_stops = timing.stops.clone();
    sorted_stops.sort_by(|left, right| left.0.partial_cmp(&right.0).unwrap());

    let mut offset = timing.offset;
    let mut stops: Vec<(f64, f64)> = Vec::new();
    for &(beat, pause) in &sorted_stops {
        if beat < 0.0 {
            offset -= pause;
        } else {
            stops.push((beat, pause));
        }
    }

    let mut bpm = 0.0_f64;
    let mut index = 0;
    while index < bpms.len() && bpms[index].0 <= 0.0 {
        bpm = bpms[index].1;
        index += 1;
    }
    if bpm == 0.0 {
        if index == bpms.len() {
            return Err(UrcError::new("syntax", 1, "no BPM in simfile"));
        }
        bpm = bpms[index].1;
        index += 1;
    }

    let mut out_bpm: Vec<(f64, f64)> = Vec::new();
    let mut out_stop: Vec<(f64, f64)> = Vec::new();
    let mut out_warp: Vec<(f64, f64)> = Vec::new();
    if bpm > 0.0 && bpm <= FAST_BPM_WARP {
        out_bpm.push((0.0, bpm));
    }

    let mut prevbeat = 0.0_f64;
    let mut timeofs = 0.0_f64;
    let mut warpstart = -1.0_f64;
    let mut prewarpbpm = 0.0_f64;
    let mut ibpm = index;
    let mut istop = 0;
    while ibpm < bpms.len() || istop < stops.len() {
        let change_is_bpm = istop >= stops.len()
            || (ibpm < bpms.len() && bpms[ibpm].0 <= stops[istop].0);
        let (beat, value) = if change_is_bpm { bpms[ibpm] } else { stops[istop] };

        if bpm <= FAST_BPM_WARP {
            timeofs += (beat - prevbeat) * 60.0 / bpm;
            if warpstart >= 0.0 && bpm > 0.0 && timeofs > 0.0 {
                let warpend = beat - (timeofs * bpm / 60.0);
                out_warp.push((warpstart, warpend - warpstart));
                if bpm != prewarpbpm {
                    out_bpm.push((warpstart, bpm));
                }
                warpstart = -1.0;
            }
        }
        prevbeat = beat;

        if change_is_bpm {
            if warpstart < 0.0 && !(0.0..=FAST_BPM_WARP).contains(&value) {
                warpstart = beat;
                prewarpbpm = bpm;
                timeofs = 0.0;
            } else if warpstart < 0.0 {
                out_bpm.push((beat, value));
            }
            bpm = value;
            ibpm += 1;
        } else {
            if warpstart < 0.0 && value < 0.0 {
                warpstart = beat;
                prewarpbpm = bpm;
                timeofs = value;
            } else if warpstart < 0.0 {
                out_stop.push((beat, value));
            } else {
                timeofs += value;
                if value > 0.0 && timeofs > 0.0 {
                    out_warp.push((warpstart, beat - warpstart));
                    out_stop.push((beat, timeofs));
                    if !(0.0..=FAST_BPM_WARP).contains(&bpm) {
                        warpstart = beat;
                        timeofs = 0.0;
                    } else {
                        if bpm != prewarpbpm {
                            out_bpm.push((warpstart, bpm));
                        }
                        warpstart = -1.0;
                    }
                }
            }
            istop += 1;
        }
    }

    if warpstart >= 0.0 {
        let never_ends = !(0.0..=FAST_BPM_WARP).contains(&bpm);
        let warpend = if never_ends {
            99999999.0
        } else {
            prevbeat - (timeofs * bpm / 60.0)
        };
        out_warp.push((warpstart, warpend - warpstart));
        if bpm != prewarpbpm {
            out_bpm.push((warpstart, bpm));
        }
    }

    Ok((offset, out_bpm, out_stop, out_warp))
}

fn first_non_empty<'a>(first: &'a str, second: &'a str, fallback: &'a str) -> &'a str {
    if !first.is_empty() {
        first
    } else if !second.is_empty() {
        second
    } else {
        fallback
    }
}

fn difficulty_name(difficulty: &str, description: &str) -> &'static str {
    const NAMES: &[(&str, &str)] = &[
        ("beginner", "Beginner"),
        ("easy", "Easy"),
        ("basic", "Easy"),
        ("light", "Easy"),
        ("medium", "Medium"),
        ("another", "Medium"),
        ("trick", "Medium"),
        ("standard", "Medium"),
        ("difficult", "Medium"),
        ("hard", "Hard"),
        ("ssr", "Hard"),
        ("maniac", "Hard"),
        ("heavy", "Hard"),
        ("smaniac", "Challenge"),
        ("challenge", "Challenge"),
        ("expert", "Challenge"),
        ("oni", "Challenge"),
        ("edit", "Edit"),
    ];
    let key = difficulty.trim().to_lowercase();
    let mut name = NAMES
        .iter()
        .find(|entry| entry.0 == key)
        .map(|entry| entry.1)
        .unwrap_or("Edit");
    if name == "Hard" && matches!(description.trim().to_lowercase().as_str(), "smaniac" | "challenge") {
        name = "Challenge";
    }
    name
}

fn rows(beats: f64) -> i64 {
    let value = beats * 48.0;
    if value >= 0.0 {
        (value + 0.5) as i64
    } else {
        (value - 0.5) as i64
    }
}
