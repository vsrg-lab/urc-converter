/**
 * Mapper from the O2Jam source model onto URC charts.
 */
import { UrcError } from "../../error.js";
import type { Chart, NoteType } from "../../model.js";
import { buildTiming, checkHoldOverlap, roundMs } from "../shared.js";
import type { OjnDifficulty, OjnFile } from "./model.js";

const MEASURE_MS = 240000;
const METER_TOLERANCE = 1e-6;
const VERSIONS = ["Easy", "Normal", "Hard"] as const;
const SCALES = [1, 10, 100, 1000, 10000, 100000, 1000000, 10000000, 100000000, 1000000000];

const TYPE_ORDER: Record<NoteType, number> = { N: 0, LS: 1, LE: 2, M: 3, F: 4 };

/**
 * Converts every difficulty of an OJN file into URC charts.
 */
export function convertOjn(file: OjnFile): Chart[] {
	return file.difficulties.map(difficulty => convertChart(file, difficulty));
}

function convertChart(file: OjnFile, difficulty: OjnDifficulty): Chart {
	const events = [...difficulty.events].sort(
		(left, right) => left.measure - right.measure || left.position - right.position
	);

	let time = 0;
	let bpm = file.bpm;
	let fraction = 1;
	let pointer = 0;
	let measure = 0;
	let meter: [number, number] = [4, 4];
	let meterDirty = false;
	const bpmPoints: Array<[number, number, number, number]> = [[0, canonicalBpm(bpm), 4, 4]];
	const anchors: number[] = [0];
	const notes: Array<[number, number, NoteType]> = [];
	const holds = new Map<number, number>();

	for (const event of events) {
		while (event.measure > measure) {
			if (fraction - pointer < 0)
				throw new UrcError("syntax", event.offset, "measure fraction cuts before the current position");
			time += (MEASURE_MS * (fraction - pointer)) / bpm;
			anchors.push(roundMs(time));
			if (meterDirty) {
				bpmPoints.push([roundMs(time), canonicalBpm(bpm), 4, 4]);
				meter = [4, 4];
				meterDirty = false;
			}
			measure++;
			fraction = 1;
			pointer = 0;
		}
		time += (MEASURE_MS * (event.position - pointer)) / bpm;
		pointer = event.position;

		if (event.channel === 0) {
			fraction = event.value;
			const approx = fractionMeter(event.value);
			if (approx !== null && (approx[0] !== meter[0] || approx[1] !== meter[1])) {
				meter = approx;
				meterDirty = true;
				bpmPoints.push([roundMs(time), canonicalBpm(bpm), meter[0], meter[1]]);
			}
		} else if (event.channel === 1) {
			bpmPoints.push([roundMs(time), canonicalBpm(event.value), meter[0], meter[1]]);
			bpm = event.value;
		} else 
			addNote(notes, holds, roundMs(time), event.channel - 2, event.kind);
		
	}
	for (const index of holds.values())
		notes[index][2] = "N";

	const noteTimes = notes.filter(([, , type]) => type !== "LE").map(([time]) => time);
	const firstNoteTime = noteTimes.length > 0 ? Math.min(...noteTimes) : 0;

	const timingPoints = buildTiming(
		bpmPoints,
		[],
		firstNoteTime,
		".ojn",
		anchors.find(time => time >= firstNoteTime) ?? null
	);

	notes.sort(
		(left, right) => left[0] - right[0] || left[1] - right[1] || TYPE_ORDER[left[2]] - TYPE_ORDER[right[2]]
	);
	const finalNotes = notes.map(([time, lane, type]) => ({
		timestampMs: time - firstNoteTime,
		lane,
		type
	}));
	checkHoldOverlap(finalNotes);

	return {
		formatVersion: { major: 1, minor: 1 },
		metadata: {
			original: "O2Jam",
			title: file.title || "Unknown",
			artist: file.artist || "Unknown",
			creator: file.noter || "Unknown",
			version: VERSIONS[difficulty.index]
		},
		judgment: null,
		layout: { keys: 7, specialKeys: 0, specialLanes: null },
		timing: timingPoints,
		notes: finalNotes
	};
}

function addNote(
	notes: Array<[number, number, NoteType]>,
	holds: Map<number, number>,
	ms: number,
	lane: number,
	kind: number
): void {
	if (kind === 3) {
		const index = holds.get(lane);
		if (index === undefined)
			return;
		holds.delete(lane);
		if (ms <= notes[index][0])
			notes[index][2] = "N";
		else
			notes.push([ms, lane, "LE"]);
		return;
	}
	if (holds.has(lane))
		return;
	if (kind === 2) {
		holds.set(lane, notes.length);
		notes.push([ms, lane, "LS"]);
	} else 
		notes.push([ms, lane, "N"]);
	
}

/**
 * Shortest decimal that round-trips the f32 BPM, widened to f64. Candidates
 * are enumerated explicitly instead of relying on formatter-specific
 * tie-breaking, so every language agrees.
 */
function canonicalBpm(value: number): number {
	for (let precision = 0; precision < SCALES.length; precision++) {
		const base = Math.trunc(value * SCALES[precision]);
		for (const candidate of [base, base + 1, base - 1]) {
			const text = decimal(candidate, precision);
			if (Math.fround(Number(text)) === value)
				return Number(text);
		}
	}
	return value;
}

function decimal(candidate: number, precision: number): string {
	const sign = candidate < 0 ? "-" : "";
	let digits = String(Math.abs(candidate));
	if (precision === 0)
		return sign + digits;
	digits = digits.padStart(precision + 1, "0");
	let text = `${sign}${digits.slice(0, digits.length - precision)}.${digits.slice(digits.length - precision)}`;
	while (text.endsWith("0"))
		text = text.slice(0, -1);
	if (text.endsWith("."))
		text = text.slice(0, -1);
	return text;
}

/** Meter (beats, noteValue) matching a measure fraction, if clean. */
function fractionMeter(value: number): [number, number] | null {
	for (let noteValue = 1; noteValue <= 64; noteValue++) {
		const beats = Math.round(value * noteValue);
		if (beats < 1)
			continue;
		if (Math.abs(value - beats / noteValue) <= METER_TOLERANCE) {
			const divisor = gcd(beats, noteValue);
			const reduced: [number, number] = [beats / divisor, noteValue / divisor];
			return reduced[0] === 1 && reduced[1] === 1 ? null : reduced;
		}
	}
	return null;
}

function gcd(a: number, b: number): number {
	while (b !== 0) {
		const rest = a % b;
		a = b;
		b = rest;
	}
	return a;
}
