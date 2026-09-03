/**
 * Parser for .osu text.
 */
import type { OsuBeatmap, OsuHitObject, OsuTimingPoint } from "./model.js";
import { UrcError } from "../../error.js";

const METADATA_FIELDS: Record<string, "title" | "titleUnicode" | "artist" | "artistUnicode" | "creator" | "version"> = {
	Title: "title",
	TitleUnicode: "titleUnicode",
	Artist: "artist",
	ArtistUnicode: "artistUnicode",
	Creator: "creator",
	Version: "version"
};

/**
 * Parses .osu text into a source model.
 */
export function parseOsu(text: string): OsuBeatmap {
	const beatmap: OsuBeatmap = {
		mode: 3,
		title: null,
		titleUnicode: null,
		artist: null,
		artistUnicode: null,
		creator: null,
		version: null,
		circleSize: null,
		overallDifficulty: null,
		timingPoints: [],
		hitObjects: []
	};
	let section: string | null = null;
	const body = text.startsWith("﻿") ? text.slice(1) : text;

	for (const [offset, raw] of body.replace(/\r\n?/g, "\n").split("\n").entries()) {
		const lineNo = offset + 1;
		const line = raw.trim();

		if (line.startsWith("[") && line.endsWith("]")) {
			section = line.slice(1, -1);
			continue;
		}
		if (line === "" || line.startsWith("//") || section === null)
			continue;

		switch (section) {
			case "General":
				parseGeneral(beatmap, line, lineNo);
				break;
			case "Metadata":
				parseMetadata(beatmap, line);
				break;
			case "Difficulty":
				parseDifficulty(beatmap, line, lineNo);
				break;
			case "TimingPoints":
				beatmap.timingPoints.push(parseTimingPoint(line, lineNo));
				break;
			case "HitObjects":
				beatmap.hitObjects.push(parseHitObject(line, lineNo));
				break;
		}
	}

	return beatmap;
}

function parseGeneral(beatmap: OsuBeatmap, line: string, lineNo: number): void {
	const [key, value] = splitField(line);
	if (key !== undefined && value !== undefined && key === "Mode")
		beatmap.mode = toInt(value, lineNo, "Mode");

}

function parseMetadata(beatmap: OsuBeatmap, line: string): void {
	const [key, value] = splitField(line);
	const field = key === undefined || value === undefined ? undefined : METADATA_FIELDS[key];
	if (key !== undefined && value !== undefined && field !== undefined)
		beatmap[field] = value;

}

function parseDifficulty(beatmap: OsuBeatmap, line: string, lineNo: number): void {
	const [key, value] = splitField(line);
	if (key === undefined || value === undefined)
		return;

	if (key === "CircleSize")
		beatmap.circleSize = toFloat(value, lineNo, "CircleSize");
	else if (key === "OverallDifficulty")
		beatmap.overallDifficulty = toFloat(value, lineNo, "OverallDifficulty");

}

function parseTimingPoint(line: string, lineNo: number): OsuTimingPoint {
	const fields = line.split(",").map(field => field.trim());
	if (fields.length < 2)
		throw new UrcError("syntax", lineNo, `timing point needs at least 2 fields: ${line}`);

	return {
		time: toInt(fields[0], lineNo, "timing time"),
		beatLength: toFloat(fields[1], lineNo, "beat length"),
		meter: fields.length > 2 && fields[2] !== "" ? toInt(fields[2], lineNo, "meter") : 4,
		uninherited:
			fields.length > 6 && fields[6] !== "" ? toInt(fields[6], lineNo, "uninherited") !== 0 : true
	};
}

function parseHitObject(line: string, lineNo: number): OsuHitObject {
	const fields = line.split(",").map(field => field.trim());
	if (fields.length < 5)
		throw new UrcError("syntax", lineNo, `hit object needs at least 5 fields: ${line}`);

	const x = toInt(fields[0], lineNo, "hit object x");
	const time = toInt(fields[2], lineNo, "hit object time");
	const typeBits = toInt(fields[3], lineNo, "hit object type");

	const isHold = (typeBits & 128) !== 0;
	if (!isHold && (typeBits & 1) === 0)
		throw new UrcError("syntax", lineNo, `unsupported hit object type: ${typeBits}`);

	let endTime: number | null = null;
	if (isHold) {
		if (fields.length < 6)
			throw new UrcError("syntax", lineNo, `hold note needs an end time: ${line}`);

		endTime = toInt(fields[5].split(":", 1)[0].trim(), lineNo, "hold end time");
	}

	return { x, time, isHold, endTime };
}

function splitField(line: string): [string | undefined, string | undefined] {
	const separator = line.indexOf(":");
	if (separator === -1)
		return [undefined, undefined];

	return [line.slice(0, separator).trim(), line.slice(separator + 1).trim()];
}

function toInt(token: string, lineNo: number, label: string): number {
	const value = toFloat(token, lineNo, label);
	return Math.round(value);
}

function toFloat(token: string, lineNo: number, label: string): number {
	if (token.trim() === "")
		throw new UrcError("syntax", lineNo, `invalid ${label}: ${token}`);

	const value = Number(token);
	if (Number.isNaN(value))
		throw new UrcError("syntax", lineNo, `invalid ${label}: ${token}`);

	return value;
}
