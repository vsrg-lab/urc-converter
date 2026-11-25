/**
 * Mapper from the osu!mania source model onto a URC chart.
 */
import { UrcError } from "../../error.js";
import type { Chart, Note, NoteType } from "../../model.js";
import { ZERO_LENGTH_HOLD } from "../../strings.js";
import { buildTiming, checkHoldOverlap, firstDownbeatAfter } from "../shared.js";
import type { OsuBeatmap } from "./model.js";

const JUDGMENT_RATES = [100, 100, 66.67, 33.33, 16.67, 0];
const KEY_MIN = 1;
const KEY_MAX = 18;
const TYPE_ORDER: Record<NoteType, number> = { N: 0, LS: 1, LE: 2, M: 3, F: 4 };

/**
 * Maps an osu!mania beatmap onto a URC chart.
 */
export function convertOsu(beatmap: OsuBeatmap): Chart {
	if (beatmap.mode !== 3)
		throw new UrcError("unsupported-version", 1, `unsupported game mode: ${beatmap.mode}`);

	if (beatmap.circleSize === null)
		throw new UrcError("syntax", 1, "missing CircleSize");

	if (!Number.isFinite(beatmap.circleSize) || beatmap.circleSize !== Math.trunc(beatmap.circleSize))
		throw new UrcError("syntax", 1, `CircleSize must be an integer: ${beatmap.circleSize}`);

	const keys = Math.trunc(beatmap.circleSize);
	if (keys < KEY_MIN || keys > KEY_MAX)
		throw new UrcError("syntax", 1, `CircleSize out of range: ${keys}`);

	let firstNoteTime = 0;
	if (beatmap.hitObjects.length > 0) {
		firstNoteTime = beatmap.hitObjects[0].time;
		for (let i = 1; i < beatmap.hitObjects.length; i++)
			if (beatmap.hitObjects[i].time < firstNoteTime)
				firstNoteTime = beatmap.hitObjects[i].time;
	}

	const { bpmPoints, svPoints, anchorPoints } = resolveTiming(beatmap);
	const timing = buildTiming(
		bpmPoints,
		svPoints,
		firstNoteTime,
		".osu",
		firstDownbeatAfter(anchorPoints, firstNoteTime)
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

			if (obj.endTime === obj.time)
				throw new UrcError(
					ZERO_LENGTH_HOLD,
					1,
					`zero-length hold on lane ${lane} at ${obj.time}ms`
				);

			notes.push({ timestampMs: obj.time - firstNoteTime, lane, type: "LS" });
			notes.push({ timestampMs: obj.endTime - firstNoteTime, lane, type: "LE" });
		} else
			notes.push({ timestampMs: obj.time - firstNoteTime, lane, type: "N" });
	}

	notes.sort(
		(a, b) => a.timestampMs - b.timestampMs || a.lane - b.lane || TYPE_ORDER[a.type] - TYPE_ORDER[b.type]
	);
	checkHoldOverlap(notes);

	const od = beatmap.overallDifficulty;
	let judgment = null;
	if (od !== null) {
		const windows = [16.5];
		for (const base of [64, 97, 127, 151, 188])
			windows.push(Math.max(windows[windows.length - 1], base - 3 * od + 0.5));
		judgment = { windows, rates: [...JUDGMENT_RATES] };
	}

	const title = beatmap.titleUnicode || beatmap.title;
	const artist = beatmap.artistUnicode || beatmap.artist;
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

function resolveTiming(beatmap: OsuBeatmap): {
	bpmPoints: Array<[number, number, number, number]>;
	svPoints: Array<[number, number]>;
	anchorPoints: Array<[number, number, number, number]>;
} {
	const tpByTime = new Map<number, [number, number]>();
	const svByTime = new Map<number, number>();

	for (const point of beatmap.timingPoints) {
		if (!Number.isFinite(point.beatLength) || point.beatLength === 0)
			throw new UrcError("syntax", 1, "timing point with zero beat length");
		const isSv = !point.uninherited || point.beatLength < 0;
		if (isSv) {
			if (point.beatLength > 0)
				throw new UrcError("syntax", 1, "inherited timing point with positive beat length");
			svByTime.set(point.time, -100 / point.beatLength);
		} else {
			const meter = point.meter > 0 ? point.meter : 4;
			tpByTime.set(point.time, [60000 / point.beatLength, meter]);
		}
	}

	if (tpByTime.size === 0)
		throw new UrcError("syntax", 1, ".osu: no BPM timing point");

	const allTimes = [...new Set([...tpByTime.keys(), ...svByTime.keys()])].sort((a, b) => a - b);
	const bpmPoints: Array<[number, number, number, number]> = [];
	const svPoints: Array<[number, number]> = [];
	const anchorPoints: Array<[number, number, number, number]> = [...tpByTime.entries()]
		.sort((a, b) => a[0] - b[0])
		.map(([time, [bpm, meter]]) => [time, bpm, meter, 4]);

	let currentBpm: number | null = null;
	let currentMeter = 4;
	let currentSv = 1;
	let lastBpm: number | null = null;
	let lastMeter = 4;
	let lastSv = 1;

	for (const time of allTimes) {
		const tp = tpByTime.get(time);
		if (svByTime.has(time))
			currentSv = svByTime.get(time) as number;
		else if (tp !== undefined)
			currentSv = 1;

		if (tp !== undefined)
			[currentBpm, currentMeter] = tp;

		if (currentBpm === null)
			continue;

		if (currentBpm !== lastBpm || currentMeter !== lastMeter) {
			bpmPoints.push([time, currentBpm, currentMeter, 4]);
			lastBpm = currentBpm;
			lastMeter = currentMeter;
		}
		if (currentSv !== lastSv) {
			svPoints.push([time, currentSv]);
			lastSv = currentSv;
		}
	}

	return { bpmPoints, svPoints, anchorPoints };
}
