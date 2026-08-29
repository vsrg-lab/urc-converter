/**
 * Quaver (.qua) source parser and converter.
 */
import { parse as parseYaml } from "yaml";

import { UrcError } from "../error.js";
import type { Chart, Note } from "../model.js";
import { buildTiming, checkHoldOverlap, roundMs } from "./shared.js";

/** `(mode, key count)` pairs; the numbering is Quaver's own and not regular. */
const MODE_KEYS: Array<readonly [number, number]> = [
	[1, 4],
	[2, 7],
	[3, 1],
	[4, 2],
	[5, 3],
	[6, 5],
	[7, 6],
	[8, 8],
	[9, 9],
	[10, 10]
];

/** One TimingPoints entry. */
export interface QuaTimingPoint {
	startTime: number;
	bpm: number;
	signature: number;
}

/** One ScrollSpeedFactors entry. */
export interface QuaSvPoint {
	startTime: number;
	multiplier: number;
}

/** One HitObjects entry. */
export interface QuaHitObject {
	startTime: number;
	lane: number;
	endTime: number;
	mine: boolean;
}

/** Source model of a .qua chart. */
export interface QuaMap {
	mode: number;
	hasScratchKey: boolean;
	title: string | null;
	artist: string | null;
	creator: string | null;
	difficultyName: string | null;
	timingPoints: QuaTimingPoint[];
	svPoints: QuaSvPoint[];
	hitObjects: QuaHitObject[];
}

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

/**
 * Maps a Quaver chart onto a URC chart.
 */
export function convertQua(qua: QuaMap): Chart {
	const modeKeys = MODE_KEYS.find(([mode]) => mode === qua.mode);
	if (modeKeys === undefined) 
		throw new UrcError("unsupported-version", 1, `unsupported Quaver mode: ${qua.mode}`);

	const keys = modeKeys[1];
	const specialKeys = qua.hasScratchKey ? 1 : 0;
	const firstNoteTime = qua.hitObjects.length === 0
		? 0
		: Math.min(...qua.hitObjects.map(obj => obj.startTime));

	const timing = buildTiming(
		qua.timingPoints.map(point => [point.startTime, point.bpm, point.signature]),
		qua.svPoints.map(point => [point.startTime, point.multiplier]),
		firstNoteTime,
		".qua"
	);

	const total = keys + specialKeys;
	const notes: Note[] = [];

	for (const obj of qua.hitObjects) {
		const lane = obj.lane - 1;
		if (lane < 0 || lane >= total) 
			throw new UrcError("syntax", 1, `lane out of range: ${obj.lane}`);
		
		if (obj.endTime !== 0 && obj.endTime < obj.startTime) 
			throw new UrcError(
				"syntax",
				1,
				`hold ends before it starts: ${obj.endTime} < ${obj.startTime}`
			);

		if (obj.endTime !== 0) {
			notes.push({ timestampMs: obj.startTime - firstNoteTime, lane, type: "LS" });
			notes.push({ timestampMs: obj.endTime - firstNoteTime, lane, type: "LE" });
		} else if (obj.mine) 
			notes.push({ timestampMs: obj.startTime - firstNoteTime, lane, type: "M" });
		else 
			notes.push({ timestampMs: obj.startTime - firstNoteTime, lane, type: "N" });
		
	}

	checkHoldOverlap(notes);

	const missing = [
		["Title", qua.title],
		["Artist", qua.artist],
		["Creator", qua.creator],
		["DifficultyName", qua.difficultyName]
	]
		.filter(([, value]) => value === null || value === "")
		.map(([label]) => label);
	if (missing.length > 0) 
		throw new UrcError("syntax", 1, `missing metadata: ${missing.join(", ")}`);

	return {
		formatVersion: { major: 1, minor: 1 },
		metadata: {
			original: "Quaver",
			title: qua.title as string,
			artist: qua.artist as string,
			creator: qua.creator as string,
			version: qua.difficultyName as string
		},
		judgment: null,
		layout: {
			keys,
			specialKeys,
			specialLanes: qua.hasScratchKey ? [keys] : null
		},
		timing,
		notes
	};
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
