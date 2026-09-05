"""Shared merge and validation helpers for converters."""

import math
from itertools import groupby

from ..error import UrcError
from ..model import Meter, Note, NoteType, TimingPoint


def round_ms(value: float) -> int:
	return int(value + 0.5) if value >= 0 else int(value - 0.5)


def first_downbeat_after(
	bpm_points: list[tuple[int, float, int, int]],
	time_ms: int,
) -> int | None:
	"""First measure boundary at or after time_ms; each BPM point anchors a grid."""
	points = sorted(bpm_points)

	for index, (time, bpm, beats, _note_value) in enumerate(points):
		next_time = points[index + 1][0] if index + 1 < len(points) else None
		if time_ms <= time:
			return round_ms(time)
		if next_time is None or time_ms < next_time:
			measure_ms = beats * 60000.0 / bpm
			if measure_ms <= 0:
				continue
			anchor = time + math.ceil((time_ms - time) / measure_ms - 1e-9) * measure_ms
			return round_ms(min(anchor, next_time) if next_time is not None else anchor)
	return None


def build_timing(
	bpm_points: list[tuple[int, float, int, int]],
	sv_points: list[tuple[int, float]],
	first_note_time: int,
	source: str,
	measure_anchor_ms: int | None = None,
) -> list[TimingPoint]:
	"""Merge BPM and SV points into shifted @Timing entries.

	Each BPM point carries its meter as (beats, note_value). A point is forced
	at measure_anchor_ms even without a state change so the measure grid
	survives the 0-clamp of the shift.
	"""
	events = sorted(
		[
			(time, index, True, bpm, beats, note_value)
			for index, (time, bpm, beats, note_value) in enumerate(bpm_points)
		]
		+ [
			(time, index, False, multiplier, 0, 0)
			for index, (time, multiplier) in enumerate(sv_points)
		],
		key=lambda event: (event[0], event[1]),
	)

	current_bpm: float | None = None
	current_meter = (4, 4)
	current_multiplier = 1.0
	last: tuple[float, float, tuple[int, int]] | None = None
	emitted: list[tuple[int, float, float, int, int]] = []

	for time, group in groupby(events, key=lambda event: event[0]):
		for _, _, is_bpm, value, beats, note_value in group:
			if is_bpm:
				current_bpm = value
				current_meter = (beats, note_value)
			else:
				current_multiplier = value

		if current_bpm is None or (current_bpm, current_multiplier, current_meter) == last:
			continue

		emitted.append((round_ms(time), current_bpm, current_multiplier, *current_meter))
		last = (current_bpm, current_multiplier, current_meter)

	if not emitted:
		raise UrcError("syntax", 1, f"{source}: no BPM timing point")

	if measure_anchor_ms is not None:
		anchor = round_ms(measure_anchor_ms)
		if all(entry[0] != anchor for entry in emitted):
			active = emitted[0]
			for entry in emitted:
				if entry[0] < anchor:
					active = entry
			emitted.append((anchor, *active[1:]))
			emitted.sort(key=lambda entry: entry[0])

	shifted: dict[int, tuple[float, float, int, int]] = {}
	for time, bpm, multiplier, beats, note_value in emitted:
		shifted[max(time - first_note_time, 0)] = (bpm, multiplier, beats, note_value)

	points = [
		TimingPoint(
			timestamp_ms=time,
			bpm=bpm,
			meter=Meter(beats=beats, note_value=note_value),
			multiplier=multiplier if multiplier != 1.0 else None,
		)
		for time, (bpm, multiplier, beats, note_value) in sorted(shifted.items())
	]

	if points[0].timestamp_ms != 0:
		points.insert(
			0,
			TimingPoint(timestamp_ms=0, bpm=points[0].bpm, meter=points[0].meter, multiplier=None),
		)

	return points


def check_hold_overlap(notes: list[Note]) -> None:
	"""Reject holds that overlap on the same lane."""
	open_lanes: set[int] = set()

	for note in sorted(notes, key=lambda item: (item.timestamp_ms, item.lane)):
		if note.type is NoteType.LS:
			if note.lane in open_lanes:
				raise UrcError("syntax", 1, f"overlapping holds on lane {note.lane}")

			open_lanes.add(note.lane)
		elif note.type is NoteType.LE:
			open_lanes.discard(note.lane)
