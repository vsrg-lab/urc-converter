/**
 * Mapper from the BMS-family source model onto a URC chart.
 */
import { UrcError } from "../../error.js";
import type { Chart, Layout, Metadata, Note, NoteType } from "../../model.js";
import { buildTiming, checkHoldOverlap, roundMs } from "../shared.js";
import type { BmsChart } from "./model.js";

const MEASURE_US = 240000000.0;
const TYPE_ORDER: Record<NoteType, number> = { N: 0, LS: 1, LE: 2, M: 3, F: 4 };

const SIDE_OF: Record<string, number> = { "1": 0, "5": 0, D: 0, "2": 1, "6": 1, E: 1 };
const SYSTEM_CHANNELS = new Set(["02", "03", "08", "09", "SC"]);

const LAYOUTS: Record<string, [number, number, number[] | null]> = {
	"5K": [5, 1, [0]],
	"7K": [7, 1, [0]],
	"10K": [10, 2, [0, 6]],
	"14K": [14, 2, [0, 8]],
	PMS9: [9, 0, null],
	PMS18: [18, 0, null]
};

/**
 * Maps a BMS-family chart onto a URC chart.
 */
export function convertBms(chart: BmsChart): Chart {
	if (chart.bpm === null || chart.bpm <= 0)
		throw new UrcError("syntax", 1, "missing or non-positive #BPM");

	let maxMeasure = -1;
	for (const m of chart.measures.keys())
		if (m > maxMeasure)
			maxMeasure = m;

	const boundaries: number[] = [0.0];
	for (let m = 0; m <= maxMeasure; m++)
		boundaries.push(boundaries[boundaries.length - 1] + (chart.rates.get(m) ?? 1.0));

	type Entry = [number, number, "bpm" | "meter" | "stop" | "scroll" | "object" | "anchor", number];
	const entries: Entry[] = [[0.0, 0, "bpm", chart.bpm]];
	const objects: Array<[number, string, string]> = [];
	const used = new Set<string>();

	for (let m = 0; m <= maxMeasure; m++) {
		const rate = chart.rates.get(m) ?? 1.0;
		const prevRate = chart.rates.get(m - 1) ?? 1.0;
		if (rate !== prevRate) {
			const beats = rate * 4.0;
			if (Math.abs(beats - Math.round(beats)) < 1e-9 && Math.round(beats) >= 1)
				entries.push([boundaries[m], 3, "meter", Math.round(beats)]);
		}

		const measureMap = chart.measures.get(m);
		if (!measureMap)
			continue;

		for (const [channel, ids] of measureMap.entries())
			for (let idx = 0; idx < ids.length; idx++) {
				const obj = ids[idx];
				const y = boundaries[m] + (idx / ids.length) * rate;

				if (SYSTEM_CHANNELS.has(channel)) {
					if (obj === "00")
						continue;
					if (channel === "03") {
						const digits = idValue(obj, chart.base);
						entries.push([y, 0, "bpm", Math.floor(digits / 36) * 16 + (digits % 36)]);
					} else if (channel === "08") {
						const bpmVal = chart.bpmDefs.get(obj);
						if (bpmVal === undefined)
							throw new UrcError("syntax", 1, `undefined #BPM${obj}`);
						entries.push([y, 0, "bpm", bpmVal]);
					} else if (channel === "09") {
						const stopVal = chart.stopDefs.get(obj);
						if (stopVal === undefined)
							throw new UrcError("syntax", 1, `undefined #STOP${obj}`);
						entries.push([y, 1, "stop", stopVal]);
					} else {
						const scrollVal = chart.scrollDefs.get(obj);
						if (scrollVal === undefined)
							throw new UrcError("syntax", 1, `undefined #SCROLL${obj}`);
						entries.push([y, 2, "scroll", scrollVal]);
					}
					continue;
				}

				const kind = channelKind(channel);
				if (kind === null)
					continue;
				if (obj !== "00")
					used.add(`${SIDE_OF[channel[0]]}:${channel[1]}`);
				if (obj === "00" && kind !== "ln")
					continue;
				objects.push([y, channel, obj]);
				entries.push([y, 4, "object", objects.length - 1]);
			}
	}

	for (const y of boundaries)
		entries.push([y, 5, "anchor", 0]);

	const mode = detectMode(chart.pms, used);

	let bpm: number | null = null;
	let beats = 4;
	let timeUs = 0.0;
	let prevY = 0.0;
	let pendingStop = 0.0;
	const timed = new Array<number>(objects.length).fill(0.0);
	const bpmPoints: Array<[number, number, number]> = [];
	const svPoints: Array<[number, number]> = [];
	const anchors: number[] = [];

	entries.sort((a, b) => a[0] - b[0] || a[1] - b[1]);

	let i = 0;
	while (i < entries.length) {
		const y = entries[i][0];
		const group: Entry[] = [];
		while (i < entries.length && entries[i][0] === y) {
			group.push(entries[i]);
			i++;
		}

		if (bpm !== null)
			timeUs += (MEASURE_US * (y - prevY)) / bpm;
		timeUs += pendingStop;
		pendingStop = 0.0;

		let newBpm: number | null = bpm;
		let newBeats = beats;
		let scroll: number | null = null;

		for (const [, , kind, val] of group)
			if (kind === "bpm")
				newBpm = val;
			else if (kind === "meter")
				newBeats = val;
			else if (kind === "stop")
				pendingStop = (MEASURE_US * val) / newBpm!;
			else if (kind === "scroll")
				scroll = val;
			else if (kind === "anchor")
				anchors.push(roundMs(timeUs / 1000.0));
			else
				timed[val] = timeUs;

		if (newBpm !== bpm || newBeats !== beats)
			bpmPoints.push([roundMs(timeUs / 1000.0), newBpm!, newBeats]);
		if (scroll !== null)
			svPoints.push([roundMs(timeUs / 1000.0), scroll]);

		bpm = newBpm;
		beats = newBeats;
		prevY = y;
	}

	const rawNotes = buildNotes(chart, mode, objects, timed);
	let firstNoteTime = 0;
	let hasFirst = false;
	for (const [t, , type] of rawNotes)
		if (type !== "LE")
			if (!hasFirst || t < firstNoteTime) {
				firstNoteTime = t;
				hasFirst = true;
			}

	const timing = buildTiming(
		bpmPoints,
		svPoints,
		firstNoteTime,
		".bms",
		anchors.find(time => time >= firstNoteTime) ?? null
	);

	const urcNotes: Note[] = rawNotes
		.map(([t, lane, type]) => ({
			timestampMs: t - firstNoteTime,
			lane,
			type
		}))
		.sort((a, b) => a.timestampMs - b.timestampMs || a.lane - b.lane || TYPE_ORDER[a.type] - TYPE_ORDER[b.type]);

	checkHoldOverlap(urcNotes);

	const [keys, specialKeys, specialLanes] = LAYOUTS[mode];
	const layout: Layout = {
		keys,
		specialKeys,
		specialLanes: specialLanes ? [...specialLanes] : null
	};

	const metadata: Metadata = {
		original: chart.pms ? "PMS" : "BMS",
		title: chart.title ?? "Unknown",
		artist: chart.artist ?? "Unknown",
		creator: "Unknown",
		version: chart.playLevel ?? "Unknown"
	};

	return {
		formatVersion: { major: 1, minor: 1 },
		metadata,
		judgment: null,
		layout,
		timing,
		notes: urcNotes
	};
}

function pairLongNotes(
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

function buildNotes(
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

function channelKind(channel: string): "visible" | "ln" | "mine" | null {
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

function detectMode(pms: boolean, used: Set<string>): string {
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

function getLane(mode: string, channel: string): number | undefined {
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

function idValue(text: string, base: number): number {
	function digit(c: string): number {
		if (c >= "0" && c <= "9")
			return c.charCodeAt(0) - 48;
		if (c >= "A" && c <= "Z")
			return c.charCodeAt(0) - 65 + 10;
		return c.charCodeAt(0) - 97 + 36;
	}
	return digit(text[0]) * base + digit(text[1]);
}
