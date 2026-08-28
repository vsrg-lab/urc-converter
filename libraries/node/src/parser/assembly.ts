import { UrcError } from "../error.js";
import type { Chart, Judgment, Layout, Note, NoteType } from "../model.js";
import { NOTE_TOKENS, REQUIRED_SECTIONS, rule } from "../strings.js";
import type { ParseState } from "./state.js";

const NOTE_TYPES: Readonly<Record<string, NoteType>> = Object.fromEntries(
	NOTE_TOKENS.map(token => [token, token])
);

/**
 * Validates notes (Phase C) and assembles the final Chart.
 */
export function build(state: ParseState, endLine: number): Chart {
	for (const name of REQUIRED_SECTIONS) 
		if (!state.seen.has(name)) 
			throw new UrcError(rule(2), endLine, `missing required section: ${name}`);
	
	const layoutPair = state.layoutType;
	if (layoutPair === null) 
		throw new Error("internal error: Layout finalized without Type");
	
	const [keys, specialKeys] = layoutPair;
	const layout: Layout = { keys, specialKeys, specialLanes: state.special };
	const total = keys + specialKeys;

	const ordered = [...state.notes].sort((a, b) => a.timestamp - b.timestamp || a.lane - b.lane);
	const notes: Note[] = [];
	const openLs = new Map<number, number>();
	for (const raw of ordered) {
		if (raw.timestamp < 0) 
			throw new UrcError(rule(22), raw.line, "note timestamps must be non-negative");
		
		if (raw.lane < 0 || raw.lane >= total) 
			throw new UrcError(rule(18), raw.line, `lane out of range: ${raw.lane}`);
		
		const noteType = NOTE_TYPES[raw.typeToken];
		if (noteType === undefined) 
			throw new UrcError(rule(19), raw.line, `unknown note type: '${raw.typeToken}'`);
		
		if (noteType === "LE") {
			if (!openLs.has(raw.lane)) 
				throw new UrcError(rule(20), raw.line, `LE without an open LS on lane ${raw.lane}`);
			
			openLs.delete(raw.lane);
		} else if (noteType === "LS") {
			if (openLs.has(raw.lane)) 
				throw new UrcError(rule(21), raw.line, `overlapping long notes on lane ${raw.lane}`);
			
			openLs.set(raw.lane, raw.line);
		}
		notes.push({ timestampMs: raw.timestamp, lane: raw.lane, type: noteType });
	}
	const firstOpen = openLs.entries().next().value;
	if (firstOpen !== undefined) {
		const [lane, line] = firstOpen;
		throw new UrcError(rule(20), line, `unterminated LS on lane ${lane}`);
	}

	let judgment: Judgment | null = null;
	if (state.windows !== null && state.rates !== null) 
		judgment = { windows: state.windows, rates: state.rates };
	
	const metadataValue = (name: string): string => {
		const value = state.metadata.get(name);
		if (value === undefined) 
			throw new Error(`internal error: metadata field ${name} is validated`);
		
		return value;
	};
	return {
		formatVersion: state.version,
		metadata: {
			original: metadataValue("Original"),
			title: metadataValue("Title"),
			artist: metadataValue("Artist"),
			creator: metadataValue("Creator"),
			version: metadataValue("Version")
		},
		judgment,
		layout,
		timing: state.timing,
		notes
	};
}
