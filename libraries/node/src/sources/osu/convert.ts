/**
 * Mapper from the osu!mania source model onto a URC chart.
 */
import { UrcError } from "../../error.js";
import type { Chart, Note } from "../../model.js";
import { buildTiming, checkHoldOverlap, firstDownbeatAfter } from "../shared.js";
import type { OsuBeatmap } from "./model.js";

const JUDGMENT_RATES = [100, 100, 66.67, 33.33, 16.67, 0];
const KEY_MIN = 1;
const KEY_MAX = 18;

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

	const bpmPoints: Array<[number, number, number, number]> = beatmap.timingPoints
		.filter(point => point.uninherited)
		.map(point => [
			point.time,
			60000 / point.beatLength,
			point.meter,
			4
		]);

	const timing = buildTiming(
		bpmPoints,
		beatmap.timingPoints.filter(point => !point.uninherited).map(point => [
			point.time,
			-100 / point.beatLength
		]),
		firstNoteTime,
		".osu",
		firstDownbeatAfter(bpmPoints, firstNoteTime)
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
