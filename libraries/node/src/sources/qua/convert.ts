/**
 * Mapper from the Quaver source model onto a URC chart.
 */
import { UrcError } from "../../error.js";
import type { Chart, Note } from "../../model.js";
import { buildTiming, checkHoldOverlap, firstDownbeatAfter } from "../shared.js";
import type { QuaMap } from "./model.js";

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

	const bpmPoints: Array<[number, number, number]> = qua.timingPoints.map(
		point => [point.startTime, point.bpm, point.signature]
	);

	const timing = buildTiming(
		bpmPoints,
		qua.svPoints.map(point => [point.startTime, point.multiplier]),
		firstNoteTime,
		".qua",
		firstDownbeatAfter(bpmPoints, firstNoteTime)
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
