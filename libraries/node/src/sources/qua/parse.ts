/**
 * Parser for .qua YAML text.
 */
import { parse as parseYaml } from "yaml";

import { UrcError } from "../../error.js";
import { roundMs } from "../shared.js";
import type { QuaMap } from "./model.js";

/**
 * Parses .qua YAML text into a source model.
 */
export function parseQua(text: string): QuaMap {
	let data: unknown;
	try {
		data = parseYaml(text);
	} catch (error) {
		throw new UrcError("syntax", yamlErrorLine(error), `invalid YAML: ${String(error)}`);
	}
	if (data === null || typeof data !== "object" || Array.isArray(data))
		throw new UrcError("syntax", 1, ".qua must be a YAML mapping");

	const doc = data as Record<string, unknown>;

	const qua: QuaMap = {
		mode: intField(doc, "Mode", 1),
		hasScratchKey: doc["HasScratchKey"] === true,
		title: strField(doc, "Title"),
		artist: strField(doc, "Artist"),
		creator: strField(doc, "Creator"),
		difficultyName: strField(doc, "DifficultyName"),
		timingPoints: [],
		svPoints: [],
		hitObjects: []
	};

	for (const entry of entries(doc, "TimingPoints")) {
		let signature = intField(entry, "Signature", 4);
		if (signature === 0)
			signature = 4; // legacy unset value, restored to 4/4 by the game

		if (signature !== 3 && signature !== 4)
			throw new UrcError("syntax", 1, `unsupported time signature: ${signature}`);

		qua.timingPoints.push({
			startTime: roundMs(floatField(entry, "StartTime")),
			bpm: floatField(entry, "Bpm"),
			signature
		});
	}

	for (const entry of entries(doc, "ScrollSpeedFactors"))
		qua.svPoints.push({
			startTime: roundMs(floatField(entry, "StartTime")),
			multiplier: floatField(entry, "Multiplier")
		});

	for (const entry of entries(doc, "HitObjects"))
		qua.hitObjects.push({
			startTime: intField(entry, "StartTime", -1),
			lane: intField(entry, "Lane", 0),
			endTime: intField(entry, "EndTime", 0),
			mine: intField(entry, "Type", 0) === 1
		});

	return qua;
}

function yamlErrorLine(error: unknown): number {
	if (typeof error === "object" && error !== null && "line" in error) {
		const line = (error as { line: unknown }).line;
		if (typeof line === "number")
			return line + 1;

	}
	return 1;
}

function entries(doc: Record<string, unknown>, key: string): Array<Record<string, unknown>> {
	const value = doc[key];
	if (value === undefined || value === null)
		return [];

	const isValid = Array.isArray(value)
		&& value.every(entry => typeof entry === "object" && entry !== null && !Array.isArray(entry));
	if (!isValid)
		throw new UrcError("syntax", 1, `${key} must be a list of mappings`);

	return value as Array<Record<string, unknown>>;
}

function strField(doc: Record<string, unknown>, key: string): string | null {
	const value = doc[key];
	if (value === undefined || value === null)
		return null;

	return typeof value === "string" ? value : String(value);
}

function intField(doc: Record<string, unknown>, key: string, fallback: number): number {
	const value = doc[key];
	if (value === undefined || value === null)
		return fallback;

	const parsed = Number(value);
	if (Number.isNaN(parsed))
		throw new UrcError("syntax", 1, `invalid ${key}: ${String(value)}`);

	return Math.round(parsed);
}

function floatField(entry: Record<string, unknown>, key: string): number {
	const value = entry[key];
	if (value === undefined || value === null)
		throw new UrcError("syntax", 1, `missing ${key}`);

	const parsed = Number(value);
	if (Number.isNaN(parsed))
		throw new UrcError("syntax", 1, `invalid ${key}: ${String(value)}`);

	return parsed;
}
