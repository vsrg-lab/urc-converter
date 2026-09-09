/**
 * MSD (Music Sync Document) parser and tokenizer for StepMania files.
 */
import { UrcError } from "../../error.js";

/**
 * Splits a simfile into MSD values (#TAG:param:...;) following MsdFile.
 */
export function tokenize(text: string): string[][] {
	const values: string[][] = [];
	let params: string[] = [];
	let current = "";
	let line = "";
	let reading = false;

	const endParam = (): void => {
		params.push(current);
		current = "";
		line = "";
	};

	let i = 0;
	const n = text.length;
	while (i < n) {
		if (i + 1 < n && text[i] === "/" && text[i + 1] === "/") {
			while (i < n && text[i] !== "\n")
				i++;
			continue;
		}
		if (reading && text[i] === "#") {
			if (line.replace(/^[ \t]+/, "").replace(/[ \t]+$/, "") !== "") {
				current += "#";
				line += "#";
				i++;
				continue;
			}
			params.push(current.replace(/[ \t\r\n]+$/, ""));
			values.push(params);
			params = [];
			current = "";
			line = "";
			reading = false;
			continue;
		}
		if (!reading) {
			if (text[i] === "#") {
				reading = true;
				line = "";
			} else if (text[i] !== "\\") {
				i++;
				continue;
			} else if (i + 1 < n) {
				i += 2;
				continue;
			}
			i++;
			continue;
		}
		if (text[i] === ":")
			endParam();
		else if (text[i] === ";") {
			endParam();
			values.push(params);
			params = [];
			current = "";
			line = "";
			reading = false;
		} else if (text[i] === "\\") {
			i++;
			if (i < n) {
				current += text[i];
				line += text[i];
			}
		} else {
			current += text[i];
			line += text[i];
		}
		if (i < n && (text[i] === "\r" || text[i] === "\n"))
			line = "";
		i++;
	}

	if (reading)
		params.push(current);
	return values;
}

/**
 * Splits comma-separated key=value expressions.
 */
export function expressions(value: string, minimum: number): string[][] {
	const parts: string[][] = [];
	for (const expression of value.split(",")) {
		if (expression.trim() === "")
			continue;
		const fields = expression.split("=");
		if (fields.length < minimum)
			throw new UrcError("syntax", 1, `malformed timing expression: ${expression}`);
		parts.push(fields);
	}
	return parts;
}

/**
 * Parses beat=value pairs from a comma-separated timing string.
 */
export function pairs(value: string, skipZero = false): Array<[number, number]> {
	const entries: Array<[number, number]> = [];
	for (const parts of expressions(value, 2)) {
		if (parts.length !== 2)
			throw new UrcError("syntax", 1, `malformed timing expression: ${parts.join("=")}`);
		const entry: [number, number] = [beatValue(parts[0]), parseFloatStrict(parts[1])];
		if (!skipZero || entry[1] !== 0)
			entries.push(entry);
	}
	return entries;
}

/**
 * Parses a beat token, rejecting row-format beats.
 */
export function beatValue(token: string): number {
	if (/[rR]\s*$/.test(token))
		throw new UrcError("syntax", 1, `row-format beats are not supported: ${token}`);
	return parseFloatStrict(token);
}

/**
 * Strictly parses a floating point number token.
 */
export function parseFloatStrict(token: string): number {
	const text = token.trim();
	if (!/^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$/.test(text))
		throw new UrcError("syntax", 1, `invalid number: ${token}`);
	return Number(text);
}

/**
 * Strictly parses an integer token.
 */
export function parseIntStrict(token: string): number {
	const text = token.trim();
	if (!/^[+-]?\d+$/.test(text))
		throw new UrcError("syntax", 1, `invalid integer: ${token}`);
	return Number(text);
}
