"""Playback rate scaling for URC charts."""

import math

from .error import UrcError
from .model import (
	Chart,
	Judgment,
	Layout,
	Metadata,
	Meter,
	Note,
	NoteType,
	RateResult,
	TimingPoint,
	UrcWarning,
	Version,
)
from .strings import (
	INVALID_RATE,
	WARNING_HOLD_LENGTH_CLAMPED,
	WARNING_NOTE_GAP_CLAMPED,
)

_MIN_SCALED_RATE = 50
_MAX_SCALED_RATE = 300
_STEP_TOLERANCE = 1e-9


def _round_ms(value: float) -> int:
	return int(value + 0.5) if value >= 0 else int(value - 0.5)


def _validate_rate(rate: float) -> int:
	if not math.isfinite(rate):
		raise UrcError(
			INVALID_RATE,
			1,
			"playback rate must be between 0.50 and 3.00 in 0.01 steps",
		)
	scaled = round(rate * 100.0)
	if (
		abs(rate * 100.0 - scaled) > _STEP_TOLERANCE
		or scaled < _MIN_SCALED_RATE
		or scaled > _MAX_SCALED_RATE
	):
		raise UrcError(
			INVALID_RATE,
			1,
			"playback rate must be between 0.50 and 3.00 in 0.01 steps",
		)
	return scaled


def scale_rate(chart: Chart, rate: float) -> RateResult:
	"""Scale a chart's playback rate within 0.50x to 3.00x in 0.01 steps.

	Timestamps are rounded to integer milliseconds and BPMs are multiplied by the
	rate. Same-lane events whose interval or hold length falls below 1ms are
	pushed forward to maintain at least a 1ms gap. Warnings are emitted only
	when the input chart already contained a sub-1ms gap on the same lane.
	"""
	scaled = _validate_rate(rate)

	shifted_timing: dict[int, TimingPoint] = {}
	for point in chart.timing:
		timestamp_ms = _round_ms(point.timestamp_ms * 100.0 / scaled)
		bpm = (point.bpm * scaled) / 100.0
		shifted_timing[timestamp_ms] = TimingPoint(
			timestamp_ms=timestamp_ms,
			bpm=bpm,
			meter=Meter(beats=point.meter.beats, note_value=point.meter.note_value),
			multiplier=point.multiplier,
		)
	timing = [shifted_timing[key] for key in sorted(shifted_timing)]

	lanes: dict[int, list[Note]] = {}
	for note in sorted(chart.notes, key=lambda item: item.timestamp_ms):
		lanes.setdefault(note.lane, []).append(note)

	adjusted_notes: list[Note] = []
	warnings: list[UrcWarning] = []

	for lane in sorted(lanes):
		prev_orig_ms: int | None = None
		prev_adjusted_ms: int | None = None
		prev_type: NoteType | None = None

		for note in lanes[lane]:
			scaled_ms = _round_ms(note.timestamp_ms * 100.0 / scaled)
			if prev_adjusted_ms is not None and scaled_ms <= prev_adjusted_ms:
				adjusted_ms = prev_adjusted_ms + 1
				if prev_orig_ms is not None and note.timestamp_ms <= prev_orig_ms:
					if prev_type is NoteType.LS and note.type is NoteType.LE:
						code = WARNING_HOLD_LENGTH_CLAMPED
						message = (
							f"lane {lane}: hold end at {scaled_ms}ms adjusted to"
							f" {adjusted_ms}ms (minimum 1ms duration)"
						)
					else:
						code = WARNING_NOTE_GAP_CLAMPED
						message = (
							f"lane {lane}: note at {scaled_ms}ms adjusted to"
							f" {adjusted_ms}ms (minimum 1ms gap)"
						)
					warnings.append(
						UrcWarning(
							code=code,
							lane=lane,
							original_ms=scaled_ms,
							adjusted_ms=adjusted_ms,
							message=message,
						)
					)
			else:
				adjusted_ms = scaled_ms

			adjusted_notes.append(Note(timestamp_ms=adjusted_ms, lane=lane, type=note.type))
			prev_orig_ms = note.timestamp_ms
			prev_adjusted_ms = adjusted_ms
			prev_type = note.type

	adjusted_notes.sort(key=lambda item: (item.timestamp_ms, item.lane))
	warnings.sort(key=lambda item: (item.adjusted_ms, item.lane))

	judgment = (
		Judgment(windows=list(chart.judgment.windows), rates=list(chart.judgment.rates))
		if chart.judgment is not None
		else None
	)
	layout = Layout(
		keys=chart.layout.keys,
		special_keys=chart.layout.special_keys,
		special_lanes=(
			list(chart.layout.special_lanes) if chart.layout.special_lanes is not None else None
		),
	)
	result_chart = Chart(
		format_version=Version(
			major=chart.format_version.major, minor=chart.format_version.minor
		),
		metadata=Metadata(
			original=chart.metadata.original,
			title=chart.metadata.title,
			artist=chart.metadata.artist,
			creator=chart.metadata.creator,
			version=chart.metadata.version,
		),
		judgment=judgment,
		layout=layout,
		timing=timing,
		notes=adjusted_notes,
	)
	return RateResult(chart=result_chart, warnings=warnings)
