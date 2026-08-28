/**
 * Shared URC vocabulary.
 */

export const SYNTAX = "syntax";
export const UNSUPPORTED_VERSION = "unsupported-version";

export const SECTIONS = ["@URC", "@Metadata", "@Judgment", "@Layout", "@Timing", "@Notes"] as const;
export const [
	SECTION_URC,
	SECTION_METADATA,
	SECTION_JUDGMENT,
	SECTION_LAYOUT,
	SECTION_TIMING,
	SECTION_NOTES
] = SECTIONS;
export const SECTION_INDEX: Readonly<Record<string, number>> = Object.fromEntries(
	SECTIONS.map((name, index) => [name, index])
);
export const REQUIRED_SECTIONS = [SECTION_METADATA, SECTION_LAYOUT, SECTION_TIMING, SECTION_NOTES] as const;

export const METADATA_FIELDS = ["Original", "Title", "Artist", "Creator", "Version"] as const;
export const JUDGMENT_FIELDS = ["Window", "Rate"] as const;
export const LAYOUT_FIELDS = ["Type", "Special"] as const;
export const [JUDGMENT_FIELD_WINDOW, JUDGMENT_FIELD_RATE] = JUDGMENT_FIELDS;
export const [LAYOUT_FIELD_TYPE, LAYOUT_FIELD_SPECIAL] = LAYOUT_FIELDS;
export const NOTE_TOKENS = ["N", "LS", "LE", "M", "F"] as const;
export const SPECIAL_NONE = "None";

export const RULE_DESCRIPTIONS: Readonly<Record<number, string>> = {
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
	22: "Timestamps non-negative"
};

/**
 * Category string for spec validation rule `number` (1-22).
 */
export function rule(number: number): string {
	return `rule:${number}`;
}
