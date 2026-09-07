/**
 * SMLoader timing preprocessing and note filtering for StepMania simfiles.
 */
import type { SmNote, Timing } from "./model.js";
import { UrcError } from "../../error.js";

const FAST_BPM_WARP = 9999999;

/**
 * Converts beat position to 48th-row index.
 */
export function rows(beats: number): number {
	const value = beats * 48;
	return value >= 0 ? Math.floor(value + 0.5) : Math.ceil(value - 0.5);
}

/**
 * Merges warp segments into [start, dest) row intervals.
 */
export function warpIntervals(warpSegs: Array<[number, number]>): Array<[number, number]> {
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

/**
 * Drops notes strictly inside a warp and truncates tails that end inside one.
 */
export function filterNotes(notes: SmNote[], intervals: Array<[number, number]>): SmNote[] {
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

/**
 * Normalized timing segments: offset, BPM changes, stops, and warps.
 */
export type Segments = [number, Array<[number, number]>, Array<[number, number]>, Array<[number, number]>];

/**
 * Port of SMLoader::ProcessBPMsAndStops: normalizes negative BPMs/stops into warps.
 */
export function preprocess(timing: Timing): Segments {
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
