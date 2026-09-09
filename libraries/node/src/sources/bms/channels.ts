/**
 * BMS channel classification, mode detection, and note mapping helpers.
 */
import { UrcError } from "../../error.js";
import type { NoteType } from "../../model.js";
import { roundMs } from "../shared.js";
import type { BmsChart } from "./model.js";

export const SIDE_OF: Record<string, number> = { "1": 0, "5": 0, D: 0, "2": 1, "6": 1, E: 1 };

export const LAYOUTS: Record<string, [number, number, number[] | null]> = {
	"5K": [5, 1, [0]],
	"7K": [7, 1, [0]],
	"10K": [10, 2, [0, 6]],
	"14K": [14, 2, [0, 8]],
	PMS9: [9, 0, null],
	PMS18: [18, 0, null]
};

/**
 * Decodes a two-character channel ID or object value.
 */
export function idValue(text: string, base: number): number {
	function digit(c: string): number {
		if (c >= "0" && c <= "9")
			return c.charCodeAt(0) - 48;
		if (c >= "A" && c <= "Z")
			return c.charCodeAt(0) - 65 + 10;
		return c.charCodeAt(0) - 97 + 36;
	}
	return digit(text[0]) * base + digit(text[1]);
}

/**
 * Classifies a channel string into its note kind.
 */
export function channelKind(channel: string): "visible" | "ln" | "mine" | null {
	if (channel.length !== 2)
		return null;
	const [first, second] = channel;
	if ((first === "1" || first === "2") && second >= "1" && second <= "9")
		return "visible";
	if ((first === "5" || first === "6") && second >= "1" && second <= "9")
		return "ln";
	if ((first === "D" || first === "E") && second >= "1" && second <= "9")
		return "mine";
	return null;
}

/**
 * Detects the game mode based on chart type and used channels.
 */
export function detectMode(pms: boolean, used: Set<string>): string {
	if (pms) {
		for (const key of used) {
			const [sideStr, second] = key.split(":");
			const side = Number.parseInt(sideStr, 10);
			if (second === "6" || second === "7" || second === "8" || second === "9" || (side === 1 && second === "1"))
				return "PMS18";
		}
		return "PMS9";
	}

	let seven = false;
	let double = false;
	for (const key of used) {
		const [sideStr, second] = key.split(":");
		const side = Number.parseInt(sideStr, 10);
		if (second === "8" || second === "9")
			seven = true;
		if (side === 1)
			double = true;
	}

	if (seven && double)
		return "14K";
	if (double)
		return "10K";
	if (seven)
		return "7K";
	return "5K";
}

/**
 * Resolves a channel to its target 0-indexed lane.
 */
export function getLane(mode: string, channel: string): number | undefined {
	const side = SIDE_OF[channel[0]];
	const key = channel[1];
	if (mode === "5K" || mode === "10K") {
		if (key === "6")
			return side * 6;
		if (key >= "1" && key <= "5")
			return Number.parseInt(key, 10) + side * 6;
		return undefined;
	}
	if (mode === "7K" || mode === "14K") {
		if (key === "6")
			return side * 8;
		if (key >= "1" && key <= "5")
			return Number.parseInt(key, 10) + side * 8;
		if (key === "8" || key === "9")
			return Number.parseInt(key, 10) - 8 + 6 + side * 8;
		return undefined;
	}
	if (mode === "PMS9") {
		if (side === 0 && key >= "1" && key <= "5")
			return Number.parseInt(key, 10) - 1;
		if (side === 1 && key >= "2" && key <= "5")
			return Number.parseInt(key, 10) - 2 + 5;
		return undefined;
	}
	if (mode === "PMS18") {
		const base = side * 9;
		if (key >= "1" && key <= "5")
			return base + Number.parseInt(key, 10) - 1;
		if (key === "8")
			return base + 5;
		if (key === "9")
			return base + 6;
		if (key === "6")
			return base + 7;
		if (key === "7")
			return base + 8;
		return undefined;
	}
	return undefined;
}

/**
 * Pairs long note start and end events for a lane stream.
 */
export function pairLongNotes(
	chart: BmsChart,
	stream: Array<[number, string]>,
	lane: number,
	notes: Array<[number, number, NoteType]>
): void {
	let start: number | null = null;
	if (chart.lntype === 1)
		for (const [time, obj] of stream) {
			if (obj === "00")
				continue;
			if (start === null)
				start = time;
			else {
				notes.push([roundMs(start / 1000.0), lane, "LS"]);
				notes.push([roundMs(time / 1000.0), lane, "LE"]);
				start = null;
			}
		}
	else
		for (const [time, obj] of stream)
			if (obj === "00") {
				if (start !== null) {
					notes.push([roundMs(start / 1000.0), lane, "LS"]);
					notes.push([roundMs(time / 1000.0), lane, "LE"]);
					start = null;
				}
			} else if (start === null)
				start = time;

	if (start !== null)
		throw new UrcError("syntax", 1, `long note on lane ${lane} has no end`);
}

/**
 * Builds URC note events from parsed BMS object streams.
 */
export function buildNotes(
	chart: BmsChart,
	mode: string,
	objects: Array<[number, string, string]>,
	timed: number[]
): Array<[number, number, NoteType]> {
	const streams = new Map<string, Array<[number, string]>>();
	for (let index = 0; index < objects.length; index++) {
		const channel = objects[index][1];
		let stream = streams.get(channel);
		if (!stream) {
			stream = [];
			streams.set(channel, stream);
		}
		stream.push([timed[index], objects[index][2]]);
	}

	const notes: Array<[number, number, NoteType]> = [];
	for (const [channel, stream] of streams.entries()) {
		const lane = getLane(mode, channel);
		if (lane === undefined)
			continue;
		const kind = channelKind(channel);

		if (kind === "mine")
			for (const [time] of stream)
				notes.push([roundMs(time / 1000.0), lane, "M"]);
		else if (kind === "ln")
			pairLongNotes(chart, stream, lane, notes);
		else {
			let pending: number | null = null;
			for (const [time, obj] of stream)
				if (chart.lnobj !== null && obj === chart.lnobj && pending !== null) {
					notes.push([roundMs(pending / 1000.0), lane, "LS"]);
					notes.push([roundMs(time / 1000.0), lane, "LE"]);
					pending = null;
				} else {
					if (pending !== null)
						notes.push([roundMs(pending / 1000.0), lane, "N"]);
					pending = time;
				}
			if (pending !== null)
				notes.push([roundMs(pending / 1000.0), lane, "N"]);
		}
	}
	return notes;
}
