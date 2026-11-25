/**
 * Mapper from the Quaver source model onto a URC chart.
 */
import { UrcError } from "../../error.js";
import type { Chart, Note } from "../../model.js";
import { ZERO_LENGTH_HOLD } from "../../strings.js";
import { buildTiming, checkHoldOverlap, firstDownbeatAfter } from "../shared.js";
import type { QuaHitObject, QuaMap, QuaTimingPoint } from "./model.js";

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
	let firstNoteTime = 0;
	if (qua.hitObjects.length > 0) {
		firstNoteTime = qua.hitObjects[0].startTime;
		for (let i = 1; i < qua.hitObjects.length; i++)
			if (qua.hitObjects[i].startTime < firstNoteTime)
				firstNoteTime = qua.hitObjects[i].startTime;
	}

	const { bpmPoints, svPoints, anchorPoints } = resolveTiming(qua, firstNoteTime);
	const timing = buildTiming(
		bpmPoints,
		svPoints,
		firstNoteTime,
		".qua",
		firstDownbeatAfter(anchorPoints, firstNoteTime)
	);

	const total = keys + specialKeys;
	const notes: Note[] = [];

	for (const obj of qua.hitObjects) {
		const lane = obj.lane - 1;
		if (lane < 0 || lane >= total)
			throw new UrcError("syntax", 1, `lane out of range: ${obj.lane}`);

		if (obj.mine)
			notes.push({ timestampMs: obj.startTime - firstNoteTime, lane, type: "M" });
		else if (obj.endTime !== 0) {
			if (obj.endTime < obj.startTime)
				throw new UrcError(
					"syntax",
					1,
					`hold ends before it starts: ${obj.endTime} < ${obj.startTime}`
				);
			if (obj.endTime === obj.startTime)
				throw new UrcError(
					ZERO_LENGTH_HOLD,
					1,
					`zero-length hold on lane ${lane} at ${obj.startTime}ms`
				);
			notes.push({ timestampMs: obj.startTime - firstNoteTime, lane, type: "LS" });
			notes.push({ timestampMs: obj.endTime - firstNoteTime, lane, type: "LE" });
		} else
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

function commonBpm(nonzeroTps: QuaTimingPoint[], hitObjects: QuaHitObject[]): number {
	if (hitObjects.length === 0)
		return Math.abs(nonzeroTps[0].bpm);

	let lastTime = !hitObjects[0].mine && hitObjects[0].endTime > 0
		? hitObjects[0].endTime
		: hitObjects[0].startTime;
	for (let i = 1; i < hitObjects.length; i++) {
		const obj = hitObjects[i];
		const end = !obj.mine && obj.endTime > 0 ? obj.endTime : obj.startTime;
		if (end > lastTime)
			lastTime = end;
	}

	const durations = new Map<number, number>();
	for (let i = nonzeroTps.length - 1; i >= 0; i--) {
		const point = nonzeroTps[i];
		if (point.startTime > lastTime)
			continue;
		const duration = lastTime - (i === 0 ? 0 : point.startTime);
		lastTime = point.startTime;
		const bpm = Math.abs(point.bpm);
		durations.set(bpm, (durations.get(bpm) ?? 0) + duration);
	}

	if (durations.size === 0)
		return Math.abs(nonzeroTps[0].bpm);

	let bestBpm = Math.abs(nonzeroTps[0].bpm);
	let bestDuration = -1;
	for (const [bpm, duration] of durations.entries())
		if (duration > bestDuration) {
			bestDuration = duration;
			bestBpm = bpm;
		}

	return bestBpm;
}

function resolveTiming(
	qua: QuaMap,
	firstNoteTime: number
): {
	bpmPoints: Array<[number, number, number, number]>;
	svPoints: Array<[number, number]>;
	anchorPoints: Array<[number, number, number, number]>;
} {
	if (qua.timingPoints.length === 0)
		throw new UrcError("syntax", 1, ".qua: no BPM timing point");

	const sortedTps = [...qua.timingPoints].sort((a, b) => a.startTime - b.startTime);
	const sortedSvs = [...qua.svPoints].sort((a, b) => a.startTime - b.startTime);

	const tpByTime = new Map<number, QuaTimingPoint>();
	for (const tp of sortedTps)
		tpByTime.set(tp.startTime, tp);
	const svByTime = new Map<number, number>();
	for (const sv of sortedSvs)
		svByTime.set(sv.startTime, sv.multiplier);

	const dedupTps = [...tpByTime.values()];
	const nonzeroTps = dedupTps.filter(tp => tp.bpm !== 0);
	if (nonzeroTps.length === 0)
		throw new UrcError("syntax", 1, ".qua: no positive BPM timing point");

	const fallbackBpm = Math.abs(nonzeroTps[0].bpm);
	const baseBpm = qua.bpmDoesNotAffectSv ? commonBpm(nonzeroTps, qua.hitObjects) : 0;

	const firstTp = dedupTps[0];
	let effBpm = firstTp.bpm !== 0 ? Math.abs(firstTp.bpm) : fallbackBpm;
	let bpmFactor = firstTp.bpm > 0 ? 1 : firstTp.bpm < 0 ? -1 : 0;
	let beats = firstTp.signature;
	let rawSv = qua.bpmDoesNotAffectSv ? qua.initialScrollVelocity : 1;

	const allTimes = [...new Set([...tpByTime.keys(), ...svByTime.keys()])].sort((a, b) => a - b);
	const bpmPoints: Array<[number, number, number, number]> = [];
	const svPoints: Array<[number, number]> = [];
	let lastBpm: number | null = null;
	let lastBeats = 4;
	let lastSv = 1;

	if (qua.bpmDoesNotAffectSv && allTimes[0] > firstNoteTime) {
		let initMult = effBpm === baseBpm ? rawSv : rawSv * (baseBpm / effBpm);
		if (initMult === 0)
			initMult = 0;
		if (initMult !== 1) {
			bpmPoints.push([firstNoteTime, effBpm, beats, 4]);
			svPoints.push([firstNoteTime, initMult]);
			lastBpm = effBpm;
			lastBeats = beats;
			lastSv = initMult;
		}
	}

	for (const time of allTimes) {
		const tp = tpByTime.get(time);
		if (svByTime.has(time))
			rawSv = svByTime.get(time) as number;
		else if (tp !== undefined && !qua.bpmDoesNotAffectSv)
			rawSv = 1;

		if (tp !== undefined) {
			beats = tp.signature;
			if (tp.bpm > 0) {
				effBpm = tp.bpm;
				bpmFactor = 1;
			} else if (tp.bpm < 0) {
				effBpm = Math.abs(tp.bpm);
				bpmFactor = -1;
			} else
				bpmFactor = 0;
		}

		let effMult = qua.bpmDoesNotAffectSv
			? effBpm === baseBpm ? rawSv : rawSv * (baseBpm / effBpm)
			: rawSv * bpmFactor;
		if (effMult === 0)
			effMult = 0;

		if (effBpm !== lastBpm || beats !== lastBeats) {
			bpmPoints.push([time, effBpm, beats, 4]);
			lastBpm = effBpm;
			lastBeats = beats;
		}
		if (effMult !== lastSv) {
			svPoints.push([time, effMult]);
			lastSv = effMult;
		}
	}

	const anchorPoints: Array<[number, number, number, number]> = nonzeroTps.map(
		tp => [tp.startTime, Math.abs(tp.bpm), tp.signature, 4]
	);
	return { bpmPoints, svPoints, anchorPoints };
}
