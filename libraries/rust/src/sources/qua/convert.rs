//! Mapper from the Quaver source model onto a URC chart.

use crate::error::{Result, UrcError};
use crate::model::{Chart, Layout, Metadata, Note, NoteType, Version};
use crate::sources::shared::{build_timing, check_hold_overlap, first_downbeat_after};

use super::model::QuaMap;

/// `(mode, key count)` pairs; the numbering is Quaver's own and not regular.
const MODE_KEYS: [(i64, u64); 10] = [
    (1, 4),
    (2, 7),
    (3, 1),
    (4, 2),
    (5, 3),
    (6, 5),
    (7, 6),
    (8, 8),
    (9, 9),
    (10, 10),
];

/// Maps a Quaver chart onto a URC chart.
pub fn convert_qua(qua: &QuaMap) -> Result<Chart> {
    let keys = MODE_KEYS
        .iter()
        .find(|(mode, _)| *mode == qua.mode)
        .map(|(_, keys)| *keys)
        .ok_or_else(|| {
            UrcError::new(
                "unsupported-version",
                1,
                format!("unsupported Quaver mode: {}", qua.mode),
            )
        })?;

    let special_keys = u64::from(qua.has_scratch_key);
    let first_note_time = qua
        .hit_objects
        .iter()
        .map(|obj| obj.start_time)
        .min()
        .unwrap_or(0);

    let bpm_points: Vec<(i64, f64, u64)> = qua
        .timing_points
        .iter()
        .map(|point| (point.start_time, point.bpm, point.signature))
        .collect();
    let sv_points: Vec<(i64, f64)> = qua
        .sv_points
        .iter()
        .map(|point| (point.start_time, point.multiplier))
        .collect();

    let timing = build_timing(
        &bpm_points,
        &sv_points,
        first_note_time,
        ".qua",
        first_downbeat_after(&bpm_points, first_note_time),
    )?;

    let total = keys + special_keys;
    let mut notes: Vec<Note> = Vec::new();

    for obj in &qua.hit_objects {
        let lane = obj.lane - 1;
        if !(0..total as i64).contains(&lane) {
            return Err(UrcError::new(
                "syntax",
                1,
                format!("lane out of range: {}", obj.lane),
            ));
        }

        if obj.end_time != 0 && obj.end_time < obj.start_time {
            return Err(UrcError::new(
                "syntax",
                1,
                format!(
                    "hold ends before it starts: {} < {}",
                    obj.end_time, obj.start_time
                ),
            ));
        }

        let lane = lane as u32;
        if obj.end_time != 0 {
            notes.push(Note {
                timestamp_ms: obj.start_time - first_note_time,
                lane,
                note_type: NoteType::Ls,
            });
            notes.push(Note {
                timestamp_ms: obj.end_time - first_note_time,
                lane,
                note_type: NoteType::Le,
            });
        } else if obj.mine {
            notes.push(Note {
                timestamp_ms: obj.start_time - first_note_time,
                lane,
                note_type: NoteType::M,
            });
        } else {
            notes.push(Note {
                timestamp_ms: obj.start_time - first_note_time,
                lane,
                note_type: NoteType::N,
            });
        }
    }

    check_hold_overlap(&notes)?;

    let missing: Vec<&str> = [
        ("Title", qua.title.as_deref()),
        ("Artist", qua.artist.as_deref()),
        ("Creator", qua.creator.as_deref()),
        ("DifficultyName", qua.difficulty_name.as_deref()),
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
            original: "Quaver".to_owned(),
            title: qua.title.clone().unwrap_or_default(),
            artist: qua.artist.clone().unwrap_or_default(),
            creator: qua.creator.clone().unwrap_or_default(),
            version: qua.difficulty_name.clone().unwrap_or_default(),
        },
        judgment: None,
        layout: Layout {
            keys,
            special_keys,
            special_lanes: qua.has_scratch_key.then(|| vec![keys as u32]),
        },
        timing,
        notes,
    })
}
