import { UrcError } from "../error.js";
import type { Meter } from "../model.js";
import { SYNTAX, rule } from "../strings.js";

const INT_RE = /^-?\d+$/;
const FLOAT_RE = /^-?\d+(\.\d+)?$/;
const UINT_RE = /^\d+$/;
const METER_RE = /^(\d+)\/(\d+)$/;
const TYPE_RE = /^(\d+)(?:\+(\d+))?$/;

/**
 * Checks that a token is entirely ASCII digits.
 */
export function validUint(token: string): boolean {
	return UINT_RE.test(token);
}

/**
 * Parses a signed decimal integer token.
 */
export function intValue(token: string, line: number): number {
	if (!INT_RE.test(token)) 
		throw new UrcError(SYNTAX, line, `invalid integer: '${token}'`);
	
	return Number(token);
}

/**
 * Parses a plain decimal float token (no exponent notation).
 */
export function floatValue(token: string, line: number): number {
	if (!FLOAT_RE.test(token)) 
		throw new UrcError(SYNTAX, line, `invalid float: '${token}'`);
	
	return Number(token);
}

/**
 * Parses a comma-separated float list; rejects empty items.
 */
export function floatList(value: string, line: number): number[] {
	const values: number[] = [];
	for (const raw of value.split(",")) {
		const token = raw.trim();
		if (token === "") 
			throw new UrcError(SYNTAX, line, "empty value in list");
		
		values.push(floatValue(token, line));
	}
	return values;
}

/**
 * Parses a `keys` or `keys+special` Type value into its pair.
 */
export function layoutType(value: string, line: number): [number, number] {
	const match = TYPE_RE.exec(value);
	if (match === null) 
		throw new UrcError(SYNTAX, line, `invalid Type value: '${value}'`);
	
	const keys = Number(match[1]);
	const hadSpecial = match[2] !== undefined;
	const special = hadSpecial ? Number(match[2]) : 0;
	if (keys < 1 || (hadSpecial && special < 1)) 
		throw new UrcError(SYNTAX, line, "Type values must be positive");
	
	return [keys, special];
}

/**
 * Parses a `beats/noteValue` meter token.
 */
export function meter(token: string, line: number): Meter {
	const match = METER_RE.exec(token);
	if (match === null) 
		throw new UrcError(rule(17), line, `invalid meter: '${token}'`);
	
	const beats = Number(match[1]);
	const noteValue = Number(match[2]);
	if (beats < 1 || noteValue < 1) 
		throw new UrcError(rule(17), line, `invalid meter: '${token}'`);
	
	return { beats, noteValue };
}
