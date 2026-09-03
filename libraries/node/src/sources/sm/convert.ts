/**
 * Mapper from the StepMania source model onto URC charts.
 */
import { UrcError } from "../../error.js";
import type { Chart, NoteType } from "../../model.js";
import { buildTiming, checkHoldOverlap, roundMs } from "../shared.js";
import type { SmChart, SmFile, SmNote, Timing } from "./model.js";
import { resolveLanes } from "./parse.js";

const MEASURE_ROWS = 192;
const FAST_BPM_WARP = 9999999;
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

function filterNotes(notes: SmNote[], intervals: Array<[number, number]>): SmNote[] {
	const kept: SmNote[] = [];
	for (const note of notes) {
		if (intervals.some(([start, dest]) => start < note.row && note.row < dest))
			continue;
		if (note.tailRow !== null) 
			for (const [start, dest] of intervals) 
				if (start < note.tailRow && note.tailRow < dest) {
					note.tailRow = start;
					break;
				}
		
		kept.push(note);
	}
	return kept;
}

function warpIntervals(warpSegs: Array<[number, number]>): Array<[number, number]> {
	const spans = warpSegs
		.map(([beat, length]) => [rows(beat), rows(beat) + rows(length)] as [number, number])
		.sort((left, right) => left[0] - right[0]);
	const merged: Array<[number, number]> = [];
	for (const [start, dest] of spans) {
		const lastSpan = merged[merged.length - 1];
		if (merged.length > 0 && start < lastSpan[1])
			lastSpan[1] = Math.max(lastSpan[1], dest);
		else
			merged.push([start, dest]);
	}
	return merged;
}

type Segments = [number, Array<[number, number]>, Array<[number, number]>, Array<[number, number]>];

/**
 * Port of SMLoader::ProcessBPMsAndStops: normalizes negative BPMs/stops into warps.
 */
function preprocess(timing: Timing): Segments {
	const bpms = [...timing.bpms].sort((left, right) => left[0] - right[0]);
	const sortedStops = [...timing.stops].sort((left, right) => left[0] - right[0]);

	let offset = timing.offset;
	const stops: Array<[number, number]> = [];
	for (const [beat, pause] of sortedStops) 
		if (beat < 0)
			offset -= pause;
		else
			stops.push([beat, pause]);

	let bpm = 0;
	let index = 0;
	while (index < bpms.length && bpms[index][0] <= 0) {
		bpm = bpms[index][1];
		index++;
	}
	if (bpm === 0) {
		if (index === bpms.length)
			throw new UrcError("syntax", 1, "no BPM in simfile");
		bpm = bpms[index][1];
		index++;
	}

	const outBpm: Array<[number, number]> = [];
	const outStop: Array<[number, number]> = [];
	const outWarp: Array<[number, number]> = [];
	if (bpm > 0 && bpm <= FAST_BPM_WARP)
		outBpm.push([0, bpm]);

	let prevbeat = 0;
	let timeofs = 0;
	let warpstart = -1;
	let prewarpbpm = 0;
	let ibpm = index;
	let istop = 0;
	while (ibpm < bpms.length || istop < stops.length) {
		const changeIsBpm =
			istop >= stops.length || (ibpm < bpms.length && bpms[ibpm][0] <= stops[istop][0]);
		const beat = changeIsBpm ? bpms[ibpm][0] : stops[istop][0];
		const value = changeIsBpm ? bpms[ibpm][1] : stops[istop][1];

		if (bpm <= FAST_BPM_WARP) {
			timeofs += ((beat - prevbeat) * 60) / bpm;
			if (warpstart >= 0 && bpm > 0 && timeofs > 0) {
				const warpend = beat - (timeofs * bpm) / 60;
				outWarp.push([warpstart, warpend - warpstart]);
				if (bpm !== prewarpbpm)
					outBpm.push([warpstart, bpm]);
				warpstart = -1;
			}
		}
		prevbeat = beat;

		if (changeIsBpm) {
			if (warpstart < 0 && (value < 0 || value > FAST_BPM_WARP)) {
				warpstart = beat;
				prewarpbpm = bpm;
				timeofs = 0;
			} else if (warpstart < 0)
				outBpm.push([beat, value]);
			bpm = value;
			ibpm++;
		} else {
			if (warpstart < 0 && value < 0) {
				warpstart = beat;
				prewarpbpm = bpm;
				timeofs = value;
			} else if (warpstart < 0)
				outStop.push([beat, value]);
			else {
				timeofs += value;
				if (value > 0 && timeofs > 0) {
					outWarp.push([warpstart, beat - warpstart]);
					outStop.push([beat, timeofs]);
					if (bpm < 0 || bpm > FAST_BPM_WARP) {
						warpstart = beat;
						timeofs = 0;
					} else {
						if (bpm !== prewarpbpm)
							outBpm.push([warpstart, bpm]);
						warpstart = -1;
					}
				}
			}
			istop++;
		}
	}

	if (warpstart >= 0) {
		const neverEnds = bpm < 0 || bpm > FAST_BPM_WARP;
		const warpend = neverEnds ? 99999999 : prevbeat - (timeofs * bpm) / 60;
		outWarp.push([warpstart, warpend - warpstart]);
		if (bpm !== prewarpbpm)
			outBpm.push([warpstart, bpm]);
	}

	return [offset, outBpm, outStop, outWarp];
}

function difficultyName(difficulty: string, description: string): string {
	let name = DIFFICULTY_NAMES[difficulty.trim().toLowerCase()] ?? "";
	if (name === "Hard" && ["smaniac", "challenge"].includes(description.trim().toLowerCase()))
		name = "Challenge";
	return name === "" ? "Edit" : name;
}

function rows(beats: number): number {
	const value = beats * 48;
	return value >= 0 ? Math.floor(value + 0.5) : Math.ceil(value - 0.5);
}
