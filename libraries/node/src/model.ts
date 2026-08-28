/**
 * URC data model shared by the parser, writer, and converters.
 */

/**
 * Type of a chart object in the @Notes section.
 */
export type NoteType = "N" | "LS" | "LE" | "M" | "F";

/**
 * File format version from the @URC header.
 */
export interface Version {
	major: number;
	minor: number;
}

/**
 * Song and chart metadata from the @Metadata section.
 */
export interface Metadata {
	original: string;
	title: string;
	artist: string;
	creator: string;
	version: string;
}

/**
 * Timing windows (ms) and scoring rates from the optional @Judgment section.
 */
export interface Judgment {
	windows: number[];
	rates: number[];
}

/**
 * Key layout from the @Layout section.
 */
export interface Layout {
	keys: number;
	specialKeys: number;
	specialLanes: number[] | null;
}

/**
 * Total lane count addressable by notes (`keys + specialKeys`).
 */
export function totalLanes(layout: Layout): number {
	return layout.keys + layout.specialKeys;
}

/**
 * Time signature numerator and denominator.
 */
export interface Meter {
	beats: number;
	noteValue: number;
}

/**
 * One @Timing entry. `multiplier` is null when omitted (= 1.0).
 */
export interface TimingPoint {
	timestampMs: number;
	bpm: number;
	meter: Meter;
	multiplier: number | null;
}

/**
 * One @Notes entry.
 */
export interface Note {
	timestampMs: number;
	lane: number;
	type: NoteType;
}

/**
 * Complete parsed URC document.
 */
export interface Chart {
	formatVersion: Version;
	metadata: Metadata;
	judgment: Judgment | null;
	layout: Layout;
	timing: TimingPoint[];
	notes: Note[];
}
