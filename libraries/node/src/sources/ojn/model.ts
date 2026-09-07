/**
 * Source model of an O2Jam (.ojn) chart file.
 */

/** One event of a note-section package, in file order. */
export interface OjnEvent {
	measure: number;
	/** Package grid position: raw event index over the package total. */
	position: number;
	channel: number;
	/** f32 payload of channels 0 (measure fraction) and 1 (BPM change). */
	value: number;
	/** Note kind from `type % 4`: 0 normal, 2 hold, 3 release. */
	kind: number;
	/** Byte offset of the event, for error locations. */
	offset: number;
}

/** Events of one difficulty section. */
export interface OjnDifficulty {
	index: number;
	events: OjnEvent[];
}

/** Parsed OJN file; the header BPM drives the timing walk. */
export interface OjnFile {
	title: string;
	artist: string;
	noter: string;
	bpm: number;
	difficulties: OjnDifficulty[];
}
