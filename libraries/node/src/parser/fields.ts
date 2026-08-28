import { UrcError } from "../error.js";
import type { TimingPoint } from "../model.js";
import {
	JUDGMENT_FIELD_RATE,
	JUDGMENT_FIELD_WINDOW,
	LAYOUT_FIELD_SPECIAL,
	LAYOUT_FIELD_TYPE,
	METADATA_FIELDS,
	SECTION_JUDGMENT,
	SECTION_LAYOUT,
	SECTION_METADATA,
	SECTION_NOTES,
	SECTION_TIMING,
	SPECIAL_NONE,
	SYNTAX,
	rule
} from "../strings.js";
import { floatValue, floatList, intValue, layoutType, meter } from "./lex.js";
import type { ParseState, RawNote } from "./state.js";

/**
 * Dispatches one content line to its section handler.
 */
export function dispatchContent(state: ParseState, section: string, text: string, line: number): void {
	switch (section) {
		case SECTION_METADATA:
			metadataField(state, text, line);
			break;
		case SECTION_JUDGMENT:
			judgmentField(state, text, line);
			break;
		case SECTION_LAYOUT:
			layoutField(state, text, line);
			break;
		case SECTION_TIMING:
			timingPoint(state, text, line);
			break;
		case SECTION_NOTES:
			noteLine(state, text, line);
			break;
		default:
			throw new UrcError(SYNTAX, line, "unexpected content after @URC header");
	}
}

function splitField(text: string): [string, string] | null {
	const index = text.indexOf(":");
	if (index === -1) 
		return null;
	
	return [text.slice(0, index).trim(), text.slice(index + 1)];
}

function metadataField(state: ParseState, text: string, line: number): void {
	const field = splitField(text);
	if (field === null) 
		throw new UrcError(SYNTAX, line, `expected 'Field: Value', got: '${text}'`);
	
	const [name, rawValue] = field;
	if (!(METADATA_FIELDS as readonly string[]).includes(name)) 
		throw new UrcError(rule(6), line, `unknown metadata field: '${name}'`);
	
	if (state.metadata.has(name)) 
		throw new UrcError(SYNTAX, line, `duplicate metadata field: '${name}'`);
	
	const value = rawValue.trim();
	if (value === "") 
		throw new UrcError(rule(5), line, `metadata field has no value: '${name}'`);
	
	state.metadata.set(name, value);
}

function judgmentField(state: ParseState, text: string, line: number): void {
	const field = splitField(text);
	if (field === null) 
		throw new UrcError(SYNTAX, line, `expected 'Field: values', got: '${text}'`);
	
	const [name, value] = field;
	if (name === JUDGMENT_FIELD_WINDOW) {
		if (state.windows !== null) 
			throw new UrcError(SYNTAX, line, "duplicate judgment field: Window");
		
		state.windows = floatList(value, line);
	} else if (name === JUDGMENT_FIELD_RATE) {
		if (state.rates !== null) 
			throw new UrcError(SYNTAX, line, "duplicate judgment field: Rate");
		
		state.rates = floatList(value, line);
	} else 
		throw new UrcError(rule(6), line, `unknown judgment field: '${name}'`);
	
}

function layoutField(state: ParseState, text: string, line: number): void {
	const field = splitField(text);
	if (field === null) 
		throw new UrcError(SYNTAX, line, `expected 'Field: Value', got: '${text}'`);
	
	const [name, value] = field;
	if (name === LAYOUT_FIELD_TYPE) {
		if (state.layoutType !== null) 
			throw new UrcError(SYNTAX, line, "duplicate layout field: Type");
		
		state.layoutType = layoutType(value.trim(), line);
	} else if (name === LAYOUT_FIELD_SPECIAL) {
		if (state.specialSeen) 
			throw new UrcError(SYNTAX, line, "duplicate layout field: Special");
		
		if (value.trim() === SPECIAL_NONE) 
			state.special = null;
		else {
			const lanes: number[] = [];
			for (const raw of value.split(",")) {
				const token = raw.trim();
				if (token === "") 
					throw new UrcError(SYNTAX, line, "empty lane in Special list");
				
				lanes.push(intValue(token, line));
			}
			state.special = lanes;
		}
		state.specialSeen = true;
	} else 
		throw new UrcError(rule(6), line, `unknown layout field: '${name}'`);
	
}

function timingPoint(state: ParseState, text: string, line: number): void {
	const fields = text.split(",").map(field => field.trim());
	if (fields.length !== 3 && fields.length !== 4) 
		throw new UrcError(SYNTAX, line, `timing point needs 3 or 4 fields, got ${fields.length}`);
	
	const timestamp = intValue(fields[0], line);
	const bpm = floatValue(fields[1], line);
	const pointMeter = meter(fields[2], line);
	let multiplier: number | null = null;
	if (fields.length === 4 && fields[3] !== "") 
		multiplier = floatValue(fields[3], line);
	
	if (state.timing.length === 0) {
		if (timestamp !== 0) 
			throw new UrcError(rule(14), line, "first timing point must be at timestamp 0");
		
	} else if (timestamp <= state.timing[state.timing.length - 1].timestampMs) 
		throw new UrcError(rule(15), line, "timing timestamps must be strictly ascending");
	
	if (bpm <= 0) 
		throw new UrcError(rule(16), line, "bpm must be positive");
	
	const point: TimingPoint = {
		timestampMs: timestamp,
		bpm,
		meter: pointMeter,
		multiplier
	};
	state.timing.push(point);
}

function noteLine(state: ParseState, text: string, line: number): void {
	const fields = text.split(",").map(field => field.trim());
	if (fields.length !== 3) 
		throw new UrcError(SYNTAX, line, `note needs 3 fields, got ${fields.length}`);
	
	const note: RawNote = {
		timestamp: intValue(fields[0], line),
		lane: intValue(fields[1], line),
		typeToken: fields[2],
		line
	};
	state.notes.push(note);
}
