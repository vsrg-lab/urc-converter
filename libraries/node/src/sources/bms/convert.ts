/**
 * Mapper from the BMS-family source model onto a URC chart.
 */
import { UrcError } from "../../error.js";
import type { Chart, Layout, Metadata, Note, NoteType } from "../../model.js";
import { buildTiming, checkHoldOverlap, roundMs } from "../shared.js";
import {
	LAYOUTS,
	SIDE_OF,
	buildNotes,
	channelKind,
	detectMode,
	idValue
} from "./channels.js";
import type { BmsChart } from "./model.js";

const MEASURE_US = 240000000.0;
const TYPE_ORDER: Record<NoteType, number> = { N: 0, LS: 1, LE: 2, M: 3, F: 4 };

const SYSTEM_CHANNELS = new Set(["02", "03", "08", "09", "SC"]);

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
	const bpmPoints: Array<[number, number, number, number]> = [];
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
			bpmPoints.push([roundMs(timeUs / 1000.0), newBpm!, newBeats, 4]);
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

