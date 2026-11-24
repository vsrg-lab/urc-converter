/**
 * Playback rate scaling for URC charts.
 */
import { UrcError } from "./error.js";
import type { Chart, Note, NoteType, RateResult, TimingPoint, UrcWarning } from "./model.js";
import { INVALID_RATE, WARNING_HOLD_LENGTH_CLAMPED, WARNING_NOTE_GAP_CLAMPED } from "./strings.js";

const MIN_SCALED_RATE = 50;
const MAX_SCALED_RATE = 300;
const STEP_TOLERANCE = 1e-9;

function roundMs(value: number): number {
	return value >= 0 ? Math.floor(value + 0.5) : Math.ceil(value - 0.5);
}

function validateRate(rate: number): number {
	if (!Number.isFinite(rate))
		throw new UrcError(
			INVALID_RATE,
			1,
			"playback rate must be between 0.50 and 3.00 in 0.01 steps"
		);

	const scaled = Math.round(rate * 100);
	if (
		Math.abs(rate * 100 - scaled) > STEP_TOLERANCE ||
		scaled < MIN_SCALED_RATE ||
		scaled > MAX_SCALED_RATE
	)
		throw new UrcError(
			INVALID_RATE,
			1,
			"playback rate must be between 0.50 and 3.00 in 0.01 steps"
		);

	return scaled;
}

/**
 * Scales a chart's playback rate within 0.50x to 3.00x in 0.01 steps.
 *
 * Timestamps are rounded to integer milliseconds and BPMs are multiplied by the
 * rate. Same-lane events whose interval or hold length falls below 1ms are
 * pushed forward to maintain at least a 1ms gap. Warnings are emitted only when
 * the input chart already contained a sub-1ms gap on the same lane.
 */
export function scaleRate(chart: Chart, rate: number): RateResult {
	const scaled = validateRate(rate);

	const shiftedTiming = new Map<number, TimingPoint>();
	for (const point of chart.timing) {
		const timestampMs = roundMs((point.timestampMs * 100) / scaled);
		const bpm = (point.bpm * scaled) / 100;
		shiftedTiming.set(timestampMs, {
			timestampMs,
			bpm,
			meter: { beats: point.meter.beats, noteValue: point.meter.noteValue },
			multiplier: point.multiplier
		});
	}
	const timing = [...shiftedTiming.entries()]
		.sort((left, right) => left[0] - right[0])
		.map(([, point]) => point);

	const orderedInput = [...chart.notes].sort((left, right) => left.timestampMs - right.timestampMs);
	const lanes = new Map<number, Note[]>();
	for (const note of orderedInput) {
		let list = lanes.get(note.lane);
		if (list === undefined) {
			list = [];
			lanes.set(note.lane, list);
		}
		list.push(note);
	}

	const adjustedNotes: Note[] = [];
	const warnings: UrcWarning[] = [];
	const laneKeys = [...lanes.keys()].sort((left, right) => left - right);

	for (const lane of laneKeys) {
		let prevOrigMs: number | null = null;
		let prevAdjustedMs: number | null = null;
		let prevType: NoteType | null = null;

		for (const note of lanes.get(lane)!) {
			const scaledMs = roundMs((note.timestampMs * 100) / scaled);
			let adjustedMs = scaledMs;

			if (prevAdjustedMs !== null && scaledMs <= prevAdjustedMs) {
				adjustedMs = prevAdjustedMs + 1;
				if (prevOrigMs !== null && note.timestampMs <= prevOrigMs) {
					const isHold = prevType === "LS" && note.type === "LE";
					const code = isHold ? WARNING_HOLD_LENGTH_CLAMPED : WARNING_NOTE_GAP_CLAMPED;
					const message = isHold
						? `lane ${lane}: hold end at ${scaledMs}ms adjusted to ${adjustedMs}ms (minimum 1ms duration)`
						: `lane ${lane}: note at ${scaledMs}ms adjusted to ${adjustedMs}ms (minimum 1ms gap)`;
					warnings.push({
						code,
						lane,
						originalMs: scaledMs,
						adjustedMs,
						message
					});
				}
			}

			adjustedNotes.push({ timestampMs: adjustedMs, lane, type: note.type });
			prevOrigMs = note.timestampMs;
			prevAdjustedMs = adjustedMs;
			prevType = note.type;
		}
	}

	adjustedNotes.sort((left, right) => left.timestampMs - right.timestampMs || left.lane - right.lane);
	warnings.sort((left, right) => left.adjustedMs - right.adjustedMs || left.lane - right.lane);

	return {
		chart: {
			formatVersion: { ...chart.formatVersion },
			metadata: { ...chart.metadata },
			judgment: chart.judgment === null
				? null
				: { windows: [...chart.judgment.windows], rates: [...chart.judgment.rates] },
			layout: {
				keys: chart.layout.keys,
				specialKeys: chart.layout.specialKeys,
				specialLanes: chart.layout.specialLanes === null ? null : [...chart.layout.specialLanes]
			},
			timing,
			notes: adjustedNotes
		},
		warnings
	};
}
