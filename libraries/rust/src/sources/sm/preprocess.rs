//! SMLoader timing preprocessing and note filtering for StepMania simfiles.

use crate::error::{Result, UrcError};

use super::model::{SmNote, Timing};

pub(crate) const FAST_BPM_WARP: f64 = 9999999.0;

pub(crate) fn rows(beats: f64) -> i64 {
    let value = beats * 48.0;
    if value >= 0.0 {
        (value + 0.5) as i64
    } else {
        (value - 0.5) as i64
    }
}

pub(crate) fn warp_intervals(warp_segs: &[(f64, f64)]) -> Vec<(i64, i64)> {
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

pub(crate) fn filter_notes(notes: &mut Vec<SmNote>, intervals: &[(i64, i64)]) {
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

pub(crate) type Segments = (f64, Vec<(f64, f64)>, Vec<(f64, f64)>, Vec<(f64, f64)>);

/// Port of `SMLoader::ProcessBPMsAndStops`: normalizes negative BPMs/stops
/// into warps.
pub(crate) fn preprocess(timing: &Timing) -> Result<Segments> {
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
