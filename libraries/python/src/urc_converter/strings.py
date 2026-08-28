"""Shared URC vocabulary."""

SYNTAX = "syntax"
UNSUPPORTED_VERSION = "unsupported-version"

SECTIONS = ("@URC", "@Metadata", "@Judgment", "@Layout", "@Timing", "@Notes")
(
    SECTION_URC,
    SECTION_METADATA,
    SECTION_JUDGMENT,
    SECTION_LAYOUT,
    SECTION_TIMING,
    SECTION_NOTES,
) = SECTIONS
SECTION_INDEX = {name: index for index, name in enumerate(SECTIONS)}
REQUIRED_SECTIONS = (SECTION_METADATA, SECTION_LAYOUT, SECTION_TIMING, SECTION_NOTES)

METADATA_FIELDS = ("Original", "Title", "Artist", "Creator", "Version")
JUDGMENT_FIELDS = ("Window", "Rate")
LAYOUT_FIELDS = ("Type", "Special")
JUDGMENT_FIELD_WINDOW, JUDGMENT_FIELD_RATE = JUDGMENT_FIELDS
LAYOUT_FIELD_TYPE, LAYOUT_FIELD_SPECIAL = LAYOUT_FIELDS
NOTE_TOKENS = ("N", "LS", "LE", "M", "F")
SPECIAL_NONE = "None"

RULE_DESCRIPTIONS = {
    1: "First line is '@URC <version>'",
    2: "All required sections present",
    3: "Sections in correct order",
    4: "All required fields present",
    5: "All required metadata fields have values",
    6: "Field names are valid",
    7: "Window and Rate have same count",
    8: "Window values ascending",
    9: "Rate values descending",
    10: "Rate values in range 0-100",
    11: "Type matches note lane count (enforced as rule 18)",
    12: "Special lanes are valid indices",
    13: "No duplicate special lanes",
    14: "First timing point at timestamp 0",
    15: "Timestamps ascending",
    16: "BPM positive",
    17: "Valid meter format",
    18: "All lanes valid (0 to key_count-1)",
    19: "Valid note types",
    20: "LS/LE properly paired",
    21: "No overlapping long notes on same lane",
    22: "Timestamps non-negative",
}


def rule(number: int) -> str:
    """Category string for spec validation rule ``number`` (1-22)."""
    return f"rule:{number}"
