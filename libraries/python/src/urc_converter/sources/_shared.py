"""Shared merge and validation helpers for converters."""

from itertools import groupby

from ..error import UrcError
from ..model import Meter, Note, NoteType, TimingPoint


def round_ms(value: float) -> int:
	return int(value + 0.5) if value >= 0 else int(value - 0.5)


def build_timing(
	bpm_points: list[tuple[int, float, int]],
	sv_points: list[tuple[int, float]],
	first_note_time: int,
	source: str,
) -> list[TimingPoint]:
	"""Merge BPM and SV points into shifted @Timing entries."""
	events = sorted(
		[(time, index, True, bpm, beats) for index, (time, bpm, beats) in enumerate(bpm_points)]
		+ [
			(time, index, False, multiplier, 0)
			for index, (time, multiplier) in enumerate(sv_points)
		],
		key=lambda event: (event[0], event[1]),
	)

	current_bpm: float | None = None
	current_beats = 4
	current_multiplier = 1.0
	last: tuple[float, float, int] | None = None
	emitted: list[tuple[int, float, float, int]] = []

	for time, group in groupby(events, key=lambda event: event[0]):
		for _, _, is_bpm, value, beats in group:
			if is_bpm:
				current_bpm, current_beats = value, beats
			else:
				current_multiplier = value

		if current_bpm is None or (current_bpm, current_multiplier, current_beats) == last:
			continue

		emitted.append((round_ms(time), current_bpm, current_multiplier, current_beats))
		last = (current_bpm, current_multiplier, current_beats)

	if not emitted:
		raise UrcError("syntax", 1, f"{source}: no BPM timing point")

	shifted: dict[int, tuple[float, float, int]] = {}
	for time, bpm, multiplier, beats in emitted:
		shifted[max(time - first_note_time, 0)] = (bpm, multiplier, beats)

	points = [
		TimingPoint(
			timestamp_ms=time,
			bpm=bpm,
			meter=Meter(beats=beats, note_value=4),
			multiplier=multiplier if multiplier != 1.0 else None,
		)
		for time, (bpm, multiplier, beats) in sorted(shifted.items())
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
			if note.lane is open_lanes:
				raise UrcError("syntax", 1, f"overlapping holds on lane {note.lane}")

			open_lanes.add(note.lane)
		elif note.type is NoteType.LE:
			open_lanes.discard(note.lane)
