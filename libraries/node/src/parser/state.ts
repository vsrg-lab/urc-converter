import type { TimingPoint, Version } from "../model.js";
import { SECTION_URC } from "../strings.js";

/**
 * A @Notes line before validation.
 */
export interface RawNote {
	timestamp: number;
	lane: number;
	typeToken: string;
	line: number;
}

/**
 * Mutable scan state shared by the parser modules.
 */
export interface ParseState {
	seen: Set<string>;
	lastIndex: number;
	version: Version;
	metadata: Map<string, string>;
	windows: number[] | null;
	rates: number[] | null;
	layoutType: [number, number] | null;
	special: number[] | null;
	specialSeen: boolean;
	timing: TimingPoint[];
	notes: RawNote[];
}

/**
 * Creates the initial state with @URC marked as seen.
 */
export function newParseState(): ParseState {
	return {
		seen: new Set([SECTION_URC]),
		lastIndex: 0,
		version: { major: 1, minor: 1 },
		metadata: new Map(),
		windows: null,
		rates: null,
		layoutType: null,
		special: null,
		specialSeen: false,
		timing: [],
		notes: []
	};
}
