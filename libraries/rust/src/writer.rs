//! Canonical URC serializer (normal form defined in the M0 plan).

use crate::model::Chart;
use crate::strings::{
    JUDGMENT_FIELD_RATE, JUDGMENT_FIELD_WINDOW, LAYOUT_FIELD_SPECIAL, LAYOUT_FIELD_TYPE,
    METADATA_FIELDS, SECTION_JUDGMENT, SECTION_LAYOUT, SECTION_METADATA, SECTION_NOTES,
    SECTION_TIMING, SPECIAL_NONE,
};

/// Serializes a [`Chart`] to canonical URC text.
pub fn write(chart: &Chart) -> String {
    let layout = &chart.layout;
    let type_text = if layout.special_keys > 0 {
        format!("{}+{}", layout.keys, layout.special_keys)
    } else {
        layout.keys.to_string()
    };
    let special_text = match &layout.special_lanes {
        None => SPECIAL_NONE.to_string(),
        Some(lanes) => lanes
            .iter()
            .map(|lane| lane.to_string())
            .collect::<Vec<_>>()
            .join(", "),
    };
    let metadata = &chart.metadata;
    let metadata_values = [
        &metadata.original,
        &metadata.title,
        &metadata.artist,
        &metadata.creator,
        &metadata.version,
    ];
    let mut lines = vec![
        format!(
            "@URC {}.{}",
            chart.format_version.major, chart.format_version.minor
        ),
        String::new(),
        SECTION_METADATA.to_string(),
    ];
    for (name, value) in METADATA_FIELDS.iter().zip(metadata_values) {
        lines.push(format!("{name}: {value}"));
    }
    if let Some(judgment) = &chart.judgment {
        lines.push(String::new());
        lines.push(SECTION_JUDGMENT.to_string());
        lines.push(format!(
            "{}: {}",
            JUDGMENT_FIELD_WINDOW,
            join_floats(&judgment.windows)
        ));
        lines.push(format!(
            "{}: {}",
            JUDGMENT_FIELD_RATE,
            join_floats(&judgment.rates)
        ));
    }
    lines.push(String::new());
    lines.push(SECTION_LAYOUT.to_string());
    lines.push(format!("{}: {type_text}", LAYOUT_FIELD_TYPE));
    lines.push(format!("{}: {special_text}", LAYOUT_FIELD_SPECIAL));
    lines.push(String::new());
    lines.push(SECTION_TIMING.to_string());
    for point in &chart.timing {
        let mut fields = vec![
            point.timestamp_ms.to_string(),
            format_float(point.bpm),
            format!("{}/{}", point.meter.beats, point.meter.note_value),
        ];
        if let Some(multiplier) = point.multiplier {
            fields.push(format_float(multiplier));
        }
        lines.push(fields.join(", "));
    }
    lines.push(String::new());
    lines.push(SECTION_NOTES.to_string());
    let mut notes = chart.notes.clone();
    notes.sort_by_key(|note| (note.timestamp_ms, note.lane));
    for note in &notes {
        lines.push(format!(
            "{}, {}, {}",
            note.timestamp_ms,
            note.lane,
            note.note_type.token()
        ));
    }
    format!("{}\n", lines.join("\n"))
}

fn join_floats(values: &[f64]) -> String {
    values
        .iter()
        .map(|&value| format_float(value))
        .collect::<Vec<_>>()
        .join(", ")
}

/// Formats a float as the shortest round-trip decimal without a forced
/// fraction part (`16.5`, `222.22`, `120`).
fn format_float(value: f64) -> String {
    let text = format!("{value:?}");
    text.strip_suffix(".0").unwrap_or(&text).to_string()
}
