//! Mapper from the osu!mania source model onto a URC chart.

use std::cmp::{max, min};

use crate::error::{Result, UrcError};
use crate::model::{Chart, Judgment, Layout, Metadata, Note, NoteType, Version};

use super::model::OsuBeatmap;
use crate::sources::shared::{build_timing, check_hold_overlap, first_downbeat_after};

const JUDGMENT_RATES: [f64; 6] = [100.0, 100.0, 66.67, 33.33, 16.67, 0.0];
const KEY_MIN: u64 = 1;
const KEY_MAX: u64 = 18;

/// Maps an osu!mania beatmap onto a URC chart.
pub fn convert_osu(beatmap: &OsuBeatmap) -> Result<Chart> {
    if beatmap.mode != 3 {
        return Err(UrcError::new(
            "unsupported-version",
            1,
            format!("unsupported game mode: {}", beatmap.mode),
        ));
    }

    let circle_size = beatmap
        .circle_size
        .ok_or_else(|| UrcError::new("syntax", 1, "missing CircleSize"))?;
    if circle_size != circle_size.trunc() {
        return Err(UrcError::new(
            "syntax",
            1,
            format!("CircleSize must be an integer: {circle_size}"),
        ));
    }

    let keys = circle_size as u64;
    if !(KEY_MIN..=KEY_MAX).contains(&keys) {
        return Err(UrcError::new(
            "syntax",
            1,
            format!("CircleSize out of range: {keys}"),
        ));
    }

    if beatmap
        .timing_points
        .iter()
        .any(|point| point.beat_length == 0.0)
    {
        return Err(UrcError::new(
            "syntax",
            1,
            "timing point with zero beat length",
        ));
    }

    let first_note_time = beatmap
        .hit_objects
        .iter()
        .map(|obj| obj.time)
        .min()
        .unwrap_or(0);

    let bpm_points: Vec<(i64, f64, u64)> = beatmap
        .timing_points
        .iter()
        .filter(|point| point.uninherited)
        .map(|point| (point.time, 60000.0 / point.beat_length, point.meter))
        .collect();
    let sv_points: Vec<(i64, f64)> = beatmap
        .timing_points
        .iter()
        .filter(|point| !point.uninherited)
        .map(|point| (point.time, -100.0 / point.beat_length))
        .collect();

    let timing = build_timing(
        &bpm_points,
        &sv_points,
        first_note_time,
        ".osu",
        first_downbeat_after(&bpm_points, first_note_time),
    )?;

    let mut notes: Vec<Note> = Vec::new();
    for obj in &beatmap.hit_objects {
        let lane = min(max(obj.x * keys as i64 / 512, 0), keys as i64 - 1) as u32;

        if obj.is_hold {
            let end_time = obj.end_time.unwrap_or_default();
            if end_time < obj.time {
                return Err(UrcError::new(
                    "syntax",
                    1,
                    format!("hold ends before it starts: {end_time} < {}", obj.time),
                ));
            }
            notes.push(Note {
                timestamp_ms: obj.time - first_note_time,
                lane,
                note_type: NoteType::Ls,
            });
            notes.push(Note {
                timestamp_ms: end_time - first_note_time,
                lane,
                note_type: NoteType::Le,
            });
        } else {
            notes.push(Note {
                timestamp_ms: obj.time - first_note_time,
                lane,
                note_type: NoteType::N,
            });
        }
    }

    check_hold_overlap(&notes)?;

    let judgment = beatmap.overall_difficulty.map(|od| Judgment {
        windows: [16.5]
            .into_iter()
            .chain(
                [64.0, 97.0, 127.0, 151.0, 188.0]
                    .into_iter()
                    .map(|base| base - 3.0 * od + 0.5),
            )
            .collect(),
        rates: JUDGMENT_RATES.to_vec(),
    });

    let title = beatmap
        .title_unicode
        .clone()
        .or_else(|| beatmap.title.clone());
    let artist = beatmap
        .artist_unicode
        .clone()
        .or_else(|| beatmap.artist.clone());
    let missing: Vec<&str> = [
        ("Title", title.as_deref()),
        ("Artist", artist.as_deref()),
        ("Creator", beatmap.creator.as_deref()),
        ("Version", beatmap.version.as_deref()),
    ]
    .into_iter()
    .filter_map(|(label, value)| value.is_none().then_some(label))
    .collect();

    if !missing.is_empty() {
        return Err(UrcError::new(
            "syntax",
            1,
            format!("missing metadata: {}", missing.join(", ")),
        ));
    }

    Ok(Chart {
        format_version: Version { major: 1, minor: 1 },
        metadata: Metadata {
            original: "osu!mania".to_owned(),
            title: title.unwrap_or_default(),
            artist: artist.unwrap_or_default(),
            creator: beatmap.creator.clone().unwrap_or_default(),
            version: beatmap.version.clone().unwrap_or_default(),
        },
        judgment,
        layout: Layout {
            keys,
            special_keys: 0,
            special_lanes: None,
        },
        timing,
        notes,
    })
}
