/**
 * Mapper from the StepMania source model onto URC charts.
 */
import { UrcError } from "../../error.js";
import type { Chart, NoteType } from "../../model.js";
import { buildTiming, checkHoldOverlap, roundMs } from "../shared.js";
import type { SmChart, SmFile, SmNote, Timing } from "./model.js";
import { resolveLanes } from "./parse.js";
import { filterNotes, preprocess, rows, warpIntervals } from "./preprocess.js";

const MEASURE_ROWS = 192;
const ROLL_TAP_SPACING_MS = 500;

const TYPE_ORDER: Record<NoteType, number> = { N: 0, LS: 1, LE: 2, M: 3, F: 4 };

const DIFFICULTY_NAMES: Record<string, string> = {
	beginner: "Beginner",
	easy: "Easy",
	basic: "Easy",
	light: "Easy",
	medium: "Medium",
	another: "Medium",
	trick: "Medium",
	standard: "Medium",
	difficult: "Medium",
	hard: "Hard",
	ssr: "Hard",
	maniac: "Hard",
	heavy: "Hard",
	smaniac: "Challenge",
	challenge: "Challenge",
	expert: "Challenge",
	oni: "Challenge",
	edit: "Edit"
};

/**
 * Converts every chart of a simfile into URC charts.
 */
export function convertSm(simfile: SmFile): Chart[] {
	return simfile.charts.map(chart => convertChart(simfile, chart));
}

type Entry = [number, number, string, unknown];

function convertChart(simfile: SmFile, chart: SmChart): Chart {
	const lanes = resolveLanes(chart.stepsType);
	const timing = chart.timing ?? simfile.timing;
	const [offset, bpmSegs, stopSegs, warpSegs] = preprocess(timing);
	const intervals = warpIntervals([...warpSegs, ...timing.warps]);
	const notes = filterNotes(chart.notes, intervals);

	const entries: Entry[] = [];
	for (const [start, dest] of intervals)
		entries.push([start, 0, "warp", dest]);
	for (const [beat, seconds] of timing.delays)
		entries.push([rows(beat), 1, "delay", seconds]);
	for (let index = 0; index < notes.length; index++) {
		entries.push([notes[index].row, 2, "note", index]);
		const tailRow = notes[index].tailRow;
		if (tailRow !== null)
			entries.push([tailRow, 2, "tail", index]);
	}
	for (const [beat, seconds] of stopSegs)
		entries.push([rows(beat), 3, "stop", seconds]);
	for (const [beat, bpm] of bpmSegs)
		entries.push([rows(beat), 4, "bpm", bpm]);
	for (const [beat, numerator, denominator] of timing.timesigs)
		entries.push([rows(beat), 5, "timesig", [numerator, denominator] as [number, number]]);
	for (const [beat, ratio] of timing.scrolls)
		entries.push([rows(beat), 6, "scroll", ratio]);
	const maxRow = entries.length > 0 ? Math.max(...entries.map(entry => entry[0])) : 0;
	for (let row = 0; row < maxRow + MEASURE_ROWS; row += MEASURE_ROWS)
		entries.push([row, 2, "anchor", null]);

	const headTimes: number[] = new Array<number>(notes.length).fill(0);
	const tailTimes = new Map<number, number>();
	const bpmPoints: Array<[number, number, number, number]> = [];
	const svPoints: Array<[number, number]> = [];
	const anchors: number[] = [];

	let seconds = -offset;
	let bpm: number | null = null;
	let meter: [number, number] = [4, 4];
	let multiplier = 1;
	let warping = false;
	let warpDest = 0;
	let prevRow: number | null = null;

	entries.sort((left, right) => left[0] - right[0] || left[1] - right[1]);
	for (let i = 0; i < entries.length; ) {
		const row = entries[i][0];
		if (prevRow !== null && !warping && bpm !== null)
			seconds += ((row - prevRow) / 48) * 60 / bpm;
		prevRow = row;
		if (warping && row >= warpDest)
			warping = false;

		let newBpm: number | null = bpm;
		let newMeter: [number, number] = meter;
		let newMultiplier = multiplier;
		for (; i < entries.length && entries[i][0] === row; i++) {
			const [, , kind, value] = entries[i];
			switch (kind) {
				case "warp": {
					const dest = value as number;
					if (warping)
						warpDest = Math.max(warpDest, dest);
					else {
						warping = true;
						warpDest = dest;
					}
					break;
				}
				case "delay":
					seconds += value as number;
					break;
				case "note":
					headTimes[value as number] = seconds;
					break;
				case "tail":
					tailTimes.set(value as number, seconds);
					break;
				case "anchor":
					anchors.push(roundMs(seconds * 1000));
					break;
				case "stop":
					seconds += value as number;
					break;
				case "bpm":
					newBpm = value as number;
					break;
				case "timesig":
					newMeter = value as [number, number];
					break;
				default:
					newMultiplier = value as number;
			}
		}

		if (newBpm !== null && (newBpm !== bpm || newMeter[0] !== meter[0] || newMeter[1] !== meter[1]))
			bpmPoints.push([roundMs(seconds * 1000), newBpm, newMeter[0], newMeter[1]]);
		if (newMultiplier !== multiplier)
			svPoints.push([roundMs(seconds * 1000), newMultiplier]);
		bpm = newBpm;
		meter = newMeter;
		multiplier = newMultiplier;
	}

	const urcNotes = buildUrcNotes(timing, notes, headTimes, tailTimes);
	const noteTimes = urcNotes.filter(([, , type]) => type !== "LE").map(([time]) => time);
	const firstNoteTime = noteTimes.length > 0 ? Math.min(...noteTimes) : 0;

	const timingPoints = buildTiming(
		bpmPoints,
		svPoints,
		firstNoteTime,
		".sm",
		anchors.find(time => time >= firstNoteTime) ?? null
	);

	urcNotes.sort(
		(left, right) => left[0] - right[0] || left[1] - right[1] || TYPE_ORDER[left[2]] - TYPE_ORDER[right[2]]
	);
	const finalNotes = urcNotes.map(([time, lane, type]) => ({
		timestampMs: time - firstNoteTime,
		lane,
		type
	}));
	checkHoldOverlap(finalNotes);

	const title = [simfile.title, simfile.subtitle].filter(part => part !== "").join(" ");
	return {
		formatVersion: { major: 1, minor: 1 },
		metadata: {
			original: "StepMania",
			title: title === "" ? "Unknown" : title,
			artist: simfile.artist === "" ? "Unknown" : simfile.artist,
			creator: chart.credit || simfile.credit || "Unknown",
			version: chart.chartname || difficultyName(chart.difficulty, chart.description)
		},
		judgment: null,
		layout: { keys: lanes, specialKeys: 0, specialLanes: null },
		timing: timingPoints,
		notes: finalNotes
	};
}

type UrcNote = [number, number, NoteType];

function buildUrcNotes(
	timing: Timing,
	notes: SmNote[],
	headTimes: number[],
	tailTimes: Map<number, number>
): UrcNote[] {
	const fakeRanges = timing.fakes.map(
		([beat, length]) => [rows(beat), rows(beat) + rows(length)] as [number, number]
	);
	const urcNotes: UrcNote[] = [];

	for (let index = 0; index < notes.length; index++) {
		const note = notes[index];
		const headMs = roundMs(headTimes[index] * 1000);
		if (fakeRanges.some(([start, end]) => start <= note.row && note.row < end)) {
			urcNotes.push([headMs, note.track, "F"]);
			continue;
		}
		if (note.kind === "hold") {
			const tailMs = roundMs((tailTimes.get(index) ?? 0) * 1000);
			if (tailMs <= headMs)
				throw new UrcError("syntax", 1, `hold on lane ${note.track} collapses to zero length`);
			urcNotes.push([headMs, note.track, "LS"]);
			urcNotes.push([tailMs, note.track, "LE"]);
		} else if (note.kind === "roll") {
			const endMs = roundMs((tailTimes.get(index) ?? 0) * 1000);
			urcNotes.push([headMs, note.track, "N"]);
			for (let tapMs = headMs + ROLL_TAP_SPACING_MS; tapMs < endMs; tapMs += ROLL_TAP_SPACING_MS)
				urcNotes.push([tapMs, note.track, "N"]);
		} else if (note.kind === "mine")
			urcNotes.push([headMs, note.track, "M"]);
		else if (note.kind === "fake")
			urcNotes.push([headMs, note.track, "F"]);
		else
			urcNotes.push([headMs, note.track, "N"]);
	}
	return urcNotes;
}

function difficultyName(difficulty: string, description: string): string {
	let name = DIFFICULTY_NAMES[difficulty.trim().toLowerCase()] ?? "";
	if (name === "Hard" && ["smaniac", "challenge"].includes(description.trim().toLowerCase()))
		name = "Challenge";
	return name === "" ? "Edit" : name;
}

