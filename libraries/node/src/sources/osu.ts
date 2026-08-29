/**
 * osu!mania (.osu) source parser and converter.
 */
import { UrcError } from "../error.js";
import type { Chart, Note } from "../model.js";
import { buildTiming, checkHoldOverlap } from "./shared.js";

const JUDGMENT_RATES = [100, 100, 66.67, 33.33, 16.67, 0];
const KEY_MIN = 1;
const KEY_MAX = 18;

const METADATA_FIELDS: Record<string, "title" | "titleUnicode" | "artist" | "artistUnicode" | "creator" | "version"> = {
	Title: "title",
	TitleUnicode: "titleUnicode",
	Artist: "artist",
	ArtistUnicode: "artistUnicode",
	Creator: "creator",
	Version: "version"
};

/** One `[TimingPoints]` entry, reduced to the fields we map. */
export interface OsuTimingPoint {
	time: number;
	beatLength: number;
	meter: number;
	uninherited: boolean;
}

/** One `[HitObjects]` entry, reduced to the fields we map. */
export interface OsuHitObject {
	x: number;
	time: number;
	isHold: boolean;
	endTime: number | null;
}

/** Source model of an osu!mania beatmap. */
export interface OsuBeatmap {
	mode: number;
	title: string | null;
	titleUnicode: string | null;
	artist: string | null;
	artistUnicode: string | null;
	creator: string | null;
	version: string | null;
	circleSize: number | null;
	overallDifficulty: number | null;
	timingPoints: OsuTimingPoint[];
	hitObjects: OsuHitObject[];
}

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

/**
 * Maps an osu!mania beatmap onto a URC chart.
 */
export function convertOsu(beatmap: OsuBeatmap): Chart {
	if (beatmap.mode !== 3) 
		throw new UrcError("unsupported-version", 1, `unsupported game mode: ${beatmap.mode}`);
	
	if (beatmap.circleSize === null) 
		throw new UrcError("syntax", 1, "missing CircleSize");
	
	if (beatmap.circleSize !== Math.trunc(beatmap.circleSize)) 
		throw new UrcError("syntax", 1, `CircleSize must be an integer: ${beatmap.circleSize}`);

	const keys = Math.trunc(beatmap.circleSize);
	if (keys < KEY_MIN || keys > KEY_MAX) 
		throw new UrcError("syntax", 1, `CircleSize out of range: ${keys}`);
	
	if (beatmap.timingPoints.some(point => point.beatLength === 0)) 
		throw new UrcError("syntax", 1, "timing point with zero beat length");

	const firstNoteTime = beatmap.hitObjects.length === 0
		? 0
		: Math.min(...beatmap.hitObjects.map(obj => obj.time));

	const timing = buildTiming(
		beatmap.timingPoints.filter(point => point.uninherited).map(point => [
			point.time,
			60000 / point.beatLength,
			point.meter
		]),
		beatmap.timingPoints.filter(point => !point.uninherited).map(point => [
			point.time,
			-100 / point.beatLength
		]),
		firstNoteTime,
		".osu"
	);

	const notes: Note[] = [];
	for (const obj of beatmap.hitObjects) {
		const lane = Math.min(Math.max(Math.floor((obj.x * keys) / 512), 0), keys - 1);

		if (obj.isHold) {
			if (obj.endTime === null || obj.endTime < obj.time) 
				throw new UrcError(
					"syntax",
					1,
					`hold ends before it starts: ${obj.endTime} < ${obj.time}`
				);
			
			notes.push({ timestampMs: obj.time - firstNoteTime, lane, type: "LS" });
			notes.push({ timestampMs: obj.endTime - firstNoteTime, lane, type: "LE" });
		} else 
			notes.push({ timestampMs: obj.time - firstNoteTime, lane, type: "N" });
		
	}

	checkHoldOverlap(notes);

	const od = beatmap.overallDifficulty;
	const judgment = od === null
		? null
		: {
			windows: [16.5, ...[64, 97, 127, 151, 188].map(base => base - 3 * od + 0.5)],
			rates: [...JUDGMENT_RATES]
		};

	const title = beatmap.titleUnicode ?? beatmap.title;
	const artist = beatmap.artistUnicode ?? beatmap.artist;
	const missing = [
		["Title", title],
		["Artist", artist],
		["Creator", beatmap.creator],
		["Version", beatmap.version]
	]
		.filter(([, value]) => value === null || value === "")
		.map(([label]) => label);
	if (missing.length > 0) 
		throw new UrcError("syntax", 1, `missing metadata: ${missing.join(", ")}`);

	return {
		formatVersion: { major: 1, minor: 1 },
		metadata: {
			original: "osu!mania",
			title: title as string,
			artist: artist as string,
			creator: beatmap.creator as string,
			version: beatmap.version as string
		},
		judgment,
		layout: { keys, specialKeys: 0, specialLanes: null },
		timing,
		notes
	};
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
