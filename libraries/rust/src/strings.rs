//! Shared URC vocabulary.

pub const SYNTAX: &str = "syntax";
pub const UNSUPPORTED_VERSION: &str = "unsupported-version";

pub const SECTION_URC: &str = "@URC";
pub const SECTION_METADATA: &str = "@Metadata";
pub const SECTION_JUDGMENT: &str = "@Judgment";
pub const SECTION_LAYOUT: &str = "@Layout";
pub const SECTION_TIMING: &str = "@Timing";
pub const SECTION_NOTES: &str = "@Notes";

pub const SECTIONS: [&str; 6] = [
    SECTION_URC,
    SECTION_METADATA,
    SECTION_JUDGMENT,
    SECTION_LAYOUT,
    SECTION_TIMING,
    SECTION_NOTES,
];
pub const REQUIRED_SECTIONS: [&str; 4] = [
    SECTION_METADATA,
    SECTION_LAYOUT,
    SECTION_TIMING,
    SECTION_NOTES,
];

/// Canonical index of a section header, or None for unknown headers.
pub fn section_index(name: &str) -> Option<usize> {
    SECTIONS.iter().position(|known| *known == name)
}

pub const METADATA_FIELDS: [&str; 5] = ["Original", "Title", "Artist", "Creator", "Version"];
pub const JUDGMENT_FIELD_WINDOW: &str = "Window";
pub const JUDGMENT_FIELD_RATE: &str = "Rate";
pub const LAYOUT_FIELD_TYPE: &str = "Type";
pub const LAYOUT_FIELD_SPECIAL: &str = "Special";
pub const NOTE_TOKENS: [&str; 5] = ["N", "LS", "LE", "M", "F"];
pub const SPECIAL_NONE: &str = "None";

/// Spec validation rule descriptions keyed by rule number.
pub const RULE_DESCRIPTIONS: [(u32, &str); 22] = [
    (1, "First line is '@URC <version>'"),
    (2, "All required sections present"),
    (3, "Sections in correct order"),
    (4, "All required fields present"),
    (5, "All required metadata fields have values"),
    (6, "Field names are valid"),
    (7, "Window and Rate have same count"),
    (8, "Window values ascending"),
    (9, "Rate values descending"),
    (10, "Rate values in range 0-100"),
    (11, "Type matches note lane count (enforced as rule 18)"),
    (12, "Special lanes are valid indices"),
    (13, "No duplicate special lanes"),
    (14, "First timing point at timestamp 0"),
    (15, "Timestamps ascending"),
    (16, "BPM positive"),
    (17, "Valid meter format"),
    (18, "All lanes valid (0 to key_count-1)"),
    (19, "Valid note types"),
    (20, "LS/LE properly paired"),
    (21, "No overlapping long notes on same lane"),
    (22, "Timestamps non-negative"),
];

/// Category string for spec validation rule `number` (1-22).
pub fn rule(number: u32) -> String {
    format!("rule:{number}")
}
