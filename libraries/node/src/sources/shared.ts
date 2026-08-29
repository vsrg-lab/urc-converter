/**
 * Shared merge and validation helpers for source format converters.
 */
import { UrcError } from "../error.js";
import type { Note, TimingPoint } from "../model.js";

interface RawTiming {
	time: number;
	bpm: number;
	multiplier: number;
	beats: number;
}

/**
 * Rounds to integer milliseconds, half away from zero.
 */
export function roundMs(value: number): number {
	return value >= 0 ? Math.floor(value + 0.5) : Math.ceil(value - 0.5);
}

/**
 * Merges BPM and SV points into shifted `@Timing` entries.
 */
export function buildTiming(
	bpmPoints: Array<[number, number, number]>,
	svPoints: Array<[number, number]>,
	firstNoteTime: number,
	source: string
): TimingPoint[] {
	type Event = [number, number, boolean, number, number];
	const events: Event[] = [
		...bpmPoints.map(([time, bpm, beats], index): Event => [time, index, true, bpm, beats]),
		...svPoints.map(([time, multiplier], index): Event => [time, index, false, multiplier, 0])
	];
	events.sort((left, right) => left[0] - right[0] || left[1] - right[1]);

	let currentBpm: number | null = null;
	let currentBeats = 4;
	let currentMultiplier = 1;
	let last: [number, number] | null = null;
	const emitted: RawTiming[] = [];

	for (const [time, , isBpm, value, beats] of events) {
		if (isBpm) {
			currentBpm = value;
			currentBeats = beats;
		} else
			currentMultiplier = value;

		if (currentBpm === null || (currentBpm === last?.[0] && currentMultiplier === last?.[1]))
			continue;

		emitted.push({
			time: roundMs(time),
			bpm: currentBpm,
			multiplier: currentMultiplier,
			beats: currentBeats
		});
		last = [currentBpm, currentMultiplier];
	}

	if (emitted.length === 0)
		throw new UrcError("syntax", 1, `${source}: no BPM timing point`);

	const shifted = new Map<number, RawTiming>();
	for (const entry of emitted)
		shifted.set(Math.max(entry.time - firstNoteTime, 0), entry);

	const points: TimingPoint[] = [...shifted.entries()]
		.sort((left, right) => left[0] - right[0])
		.map(([time, entry]) => ({
			timestampMs: time,
			bpm: entry.bpm,
			meter: { beats: entry.beats, noteValue: 4 },
			multiplier: entry.multiplier === 1 ? null : entry.multiplier
		}));

	if (points[0].timestampMs !== 0)
		points.unshift({
			timestampMs: 0,
			bpm: points[0].bpm,
			meter: points[0].meter,
			multiplier: null
		});

	return points;
}

/**
 * Rejects holds that overlap on the same lane (URC rule 21).
 */
export function checkHoldOverlap(notes: Note[]): void {
	const openLanes = new Set<number>();
	const ordered = [...notes].sort(
		(left, right) => left.timestampMs - right.timestampMs || left.lane - right.lane
	);

	for (const note of ordered)
		if (note.type === "LS") {
			if (openLanes.has(note.lane))
				throw new UrcError("syntax", 1, `overlapping holds on lane ${note.lane}`);

			openLanes.add(note.lane);
		} else if (note.type === "LE")
			openLanes.delete(note.lane);

}
