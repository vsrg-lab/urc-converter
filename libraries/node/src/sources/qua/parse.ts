/**
 * Parser for .qua YAML text.
 */
import { isMap, isScalar, isSeq, parseDocument } from "yaml";
import type { YAMLMap } from "yaml";

import { UrcError } from "../../error.js";
import { roundMs } from "../shared.js";
import type { QuaMap } from "./model.js";

const MODE_NAMES: Record<string, number> = {
	keys4: 1,
	keys7: 2,
	keys1: 3,
	keys2: 4,
	keys3: 5,
	keys5: 6,
	keys6: 7,
	keys8: 8,
	keys9: 9,
	keys10: 10
};

const SIGNATURE_NAMES: Record<string, number> = {
	quadruple: 4,
	triple: 3
};

const HIT_OBJECT_TYPES: Record<string, number> = {
	normal: 0,
	mine: 1
};

/**
 * Parses .qua YAML text into a source model.
 */
export function parseQua(text: string): QuaMap {
	const cleaned = text.replace(/^\uFEFF+/, "");
	const doc = parseDocument(cleaned, { logLevel: "silent" });
	if (doc.errors.length > 0) {
		const first = doc.errors[0];
		const line = first.linePos?.[0]?.line ?? 1;
		throw new UrcError("syntax", line, `invalid YAML: ${first.message}`);
	}
	if (!isMap(doc.contents))
		throw new UrcError("syntax", 1, ".qua must be a YAML mapping");

	const root = mapEntries(doc.contents);

	const qua: QuaMap = {
		mode: modeField(root),
		hasScratchKey: boolField(root, "HasScratchKey", false),
		bpmDoesNotAffectSv: boolField(root, "BPMDoesNotAffectScrollVelocity", false),
		initialScrollVelocity: floatField(root, "InitialScrollVelocity", 0),
		title: strField(root, "Title"),
		artist: strField(root, "Artist"),
		creator: strField(root, "Creator"),
		difficultyName: strField(root, "DifficultyName"),
		timingPoints: [],
		svPoints: [],
		hitObjects: []
	};

	for (const entry of entries(root, "TimingPoints"))
		qua.timingPoints.push({
			startTime: roundMs(floatField(entry, "StartTime", 0)),
			bpm: floatField(entry, "Bpm", 0),
			signature: signatureField(entry)
		});

	for (const entry of entries(root, "SliderVelocities"))
		qua.svPoints.push({
			startTime: roundMs(floatField(entry, "StartTime", 0)),
			multiplier: floatField(entry, "Multiplier", 0)
		});

	for (const entry of entries(root, "HitObjects"))
		qua.hitObjects.push({
			startTime: roundMs(floatField(entry, "StartTime", 0)),
			lane: roundMs(floatField(entry, "Lane", 0)),
			endTime: roundMs(floatField(entry, "EndTime", 0)),
			mine: hitObjectTypeField(entry) === 1
		});

	return qua;
}

function mapEntries(map: YAMLMap): Map<string, unknown> {
	const result = new Map<string, unknown>();
	for (const pair of map.items)
		if (isScalar(pair.key) && pair.key.value !== null)
			result.set(String(pair.key.value), pair.value);

	return result;
}

function scalarText(fields: Map<string, unknown>, key: string): string | null {
	const node = fields.get(key);
	if (node === undefined || node === null)
		return null;
	if (!isScalar(node))
		throw new UrcError("syntax", 1, `invalid ${key}`);
	if (node.value === null)
		return null;
	if (typeof node.value === "string")
		return node.value;
	return node.source ?? String(node.value);
}

function entries(fields: Map<string, unknown>, key: string): Array<Map<string, unknown>> {
	const node = fields.get(key);
	if (node === undefined || node === null)
		return [];
	if (isScalar(node) && node.value === null)
		return [];
	if (!isSeq(node))
		throw new UrcError("syntax", 1, `${key} must be a list of mappings`);

	const result: Array<Map<string, unknown>> = [];
	for (const item of node.items) {
		if (!isMap(item))
			throw new UrcError("syntax", 1, `${key} must be a list of mappings`);
		result.push(mapEntries(item));
	}
	return result;
}

function strField(fields: Map<string, unknown>, key: string): string | null {
	return scalarText(fields, key);
}

function boolField(fields: Map<string, unknown>, key: string, fallback: boolean): boolean {
	const text = scalarText(fields, key);
	if (text === null)
		return fallback;
	const lowered = text.trim().toLowerCase();
	if (lowered === "true")
		return true;
	if (lowered === "false")
		return false;
	return fallback;
}

function floatField(fields: Map<string, unknown>, key: string, fallback: number): number {
	const text = scalarText(fields, key);
	if (text === null)
		return fallback;
	const trimmed = text.trim();
	if (trimmed === "")
		throw new UrcError("syntax", 1, `invalid ${key}: ${text}`);
	const parsed = Number(trimmed);
	if (!Number.isFinite(parsed))
		throw new UrcError("syntax", 1, `invalid ${key}: ${text}`);
	return parsed;
}

function modeField(fields: Map<string, unknown>): number {
	const text = scalarText(fields, "Mode");
	if (text === null)
		return 1;
	const raw = text.trim();
	const mapped = MODE_NAMES[raw.toLowerCase()];
	if (mapped !== undefined)
		return mapped;
	if (/^-?\d+$/.test(raw))
		return Number(raw);
	throw new UrcError("unsupported-version", 1, `unsupported Quaver mode: ${raw}`);
}

function signatureField(fields: Map<string, unknown>): number {
	const text = scalarText(fields, "Signature");
	if (text === null)
		return 4;
	const raw = text.trim();
	const mapped = SIGNATURE_NAMES[raw.toLowerCase()];
	if (mapped !== undefined)
		return mapped;
	if (!/^-?\d+$/.test(raw))
		throw new UrcError("syntax", 1, `unsupported time signature: ${raw}`);
	const parsed = Number(raw);
	if (parsed === 0)
		return 4;
	if (parsed < 1)
		throw new UrcError("syntax", 1, `unsupported time signature: ${parsed}`);
	return parsed;
}

function hitObjectTypeField(fields: Map<string, unknown>): number {
	const text = scalarText(fields, "Type");
	if (text === null)
		return 0;
	const raw = text.trim();
	const mapped = HIT_OBJECT_TYPES[raw.toLowerCase()];
	if (mapped !== undefined)
		return mapped;
	if (!/^-?\d+$/.test(raw))
		throw new UrcError("syntax", 1, `unsupported hit object type: ${raw}`);
	const parsed = Number(raw);
	if (parsed !== 0 && parsed !== 1)
		throw new UrcError("syntax", 1, `unsupported hit object type: ${parsed}`);
	return parsed;
}
