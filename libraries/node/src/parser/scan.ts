import { UrcError } from "../error.js";
import type { Chart } from "../model.js";
import { SECTION_INDEX, SECTION_URC, SYNTAX, UNSUPPORTED_VERSION, rule } from "../strings.js";
import { build } from "./assembly.js";
import { finalizeSection } from "./checks.js";
import { dispatchContent } from "./fields.js";
import { newParseState } from "./state.js";
import type { ParseState } from "./state.js";

const HEADER_RE = /^@URC (\d+)\.(\d+)$/;

/**
 * Parses and validates URC text into a Chart, throwing UrcError at the first
 * failure with category "syntax", "unsupported-version", or "rule:<n>"
 * (spec rules 1-22).
 */
export function parse(text: string): Chart {
	const stripped = text.startsWith("\ufeff") ? text.slice(1) : text;
	const lines = stripped.split(/\r\n|\r|\n/);
	const state = newParseState();
	scan(lines, state);
	return build(state, lines.length + 1);
}

function scan(lines: string[], state: ParseState): void {
	let current: string = SECTION_URC;
	for (let offset = 0; offset < lines.length; offset++) {
		const lineNo = offset + 1;
		const text = lines[offset].trim();
		if (offset === 0) 
			header(text, lineNo, state);
		else if (text === "" || text.startsWith("#")) 
			continue;
		else if (text.startsWith("@")) {
			finalizeSection(state, current, lineNo);
			current = section(text, lineNo, state);
		} else 
			dispatchContent(state, current, text, lineNo);
		
	}
	finalizeSection(state, current, lines.length + 1);
}

function header(text: string, lineNo: number, state: ParseState): void {
	if (!text.startsWith("@URC")) 
		throw new UrcError(rule(1), lineNo, "first line must be '@URC <version>'");
	
	const match = HEADER_RE.exec(text);
	if (match === null) 
		throw new UrcError(SYNTAX, lineNo, `malformed @URC header: '${text}'`);
	
	const major = Number(match[1]);
	const minor = Number(match[2]);
	if (major !== 1 || minor > 1) 
		throw new UrcError(UNSUPPORTED_VERSION, lineNo, `unsupported version: ${major}.${minor}`);
	
	state.version = { major, minor };
}

function section(name: string, lineNo: number, state: ParseState): string {
	const index = SECTION_INDEX[name];
	if (index === undefined) 
		throw new UrcError(SYNTAX, lineNo, `unknown section: ${name}`);
	
	if (state.seen.has(name)) 
		throw new UrcError(rule(3), lineNo, `duplicate section: ${name}`);
	
	if (index <= state.lastIndex) 
		throw new UrcError(rule(3), lineNo, `section out of order: ${name}`);
	
	state.seen.add(name);
	state.lastIndex = index;
	return name;
}
