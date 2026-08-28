import { UrcError } from "../error.js";
import {
	METADATA_FIELDS,
	SECTION_JUDGMENT,
	SECTION_LAYOUT,
	SECTION_METADATA,
	SECTION_TIMING,
	rule
} from "../strings.js";
import type { ParseState } from "./state.js";

/**
 * Runs the finalize-time checks for a closed section.
 */
export function finalizeSection(state: ParseState, section: string, line: number): void {
	if (section === SECTION_METADATA) 
		checkMetadataComplete(state, line);
	else if (section === SECTION_JUDGMENT) 
		checkJudgment(state, line);
	else if (section === SECTION_LAYOUT) 
		checkLayout(state, line);
	else if (section === SECTION_TIMING && state.timing.length === 0) 
		throw new UrcError(rule(14), line, "first timing point must be at timestamp 0");
	
}

function checkMetadataComplete(state: ParseState, line: number): void {
	for (const name of METADATA_FIELDS) 
		if (!state.metadata.has(name)) 
			throw new UrcError(rule(4), line, `Metadata is missing field: ${name}`);
	
}

function checkJudgment(state: ParseState, line: number): void {
	const { windows, rates } = state;
	if (windows === null || rates === null) 
		throw new UrcError(rule(4), line, "Judgment requires both Window and Rate");
	
	if (windows.length !== rates.length) 
		throw new UrcError(rule(7), line, "Window and Rate must have the same count");
	
	for (let index = 1; index < windows.length; index++) 
		if (windows[index] < windows[index - 1]) 
			throw new UrcError(rule(8), line, "Window values must be ascending");
	
	for (let index = 1; index < rates.length; index++) 
		if (rates[index] > rates[index - 1]) 
			throw new UrcError(rule(9), line, "Rate values must be descending");
	
	for (const rate of rates) 
		if (rate < 0 || rate > 100) 
			throw new UrcError(rule(10), line, "Rate values must be in 0-100");
	
}

function checkLayout(state: ParseState, line: number): void {
	const { layoutType, special, specialSeen } = state;
	if (layoutType === null) 
		throw new UrcError(rule(4), line, "Layout is missing field: Type");
	
	if (!specialSeen) 
		throw new UrcError(rule(4), line, "Layout is missing field: Special");
	
	if (special === null) 
		return;
	
	const total = layoutType[0] + layoutType[1];
	for (const lane of special) 
		if (lane < 0 || lane >= total) 
			throw new UrcError(rule(12), line, `special lane out of range: ${lane}`);
	
	if (new Set(special).size !== special.length) 
		throw new UrcError(rule(13), line, "duplicate special lanes");
	
}
