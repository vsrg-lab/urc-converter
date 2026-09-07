"""Mapper from the O2Jam source model onto URC charts."""

import math
import struct

from ...error import UrcError
from ...model import Chart, Layout, Metadata, Note, NoteType, Version
from .._shared import build_timing, check_hold_overlap, round_ms
from .model import OjnDifficulty, OjnFile

_MEASURE_MS = 240000.0
_METER_TOLERANCE = 1e-6
_TYPE_ORDER = {NoteType.N: 0, NoteType.LS: 1, NoteType.LE: 2}
_VERSIONS = ("Easy", "Normal", "Hard")


def convert_ojn(file: OjnFile) -> list[Chart]:
	"""Convert every difficulty of an OJN file into URC charts."""
	return [_convert_chart(file, difficulty) for difficulty in file.difficulties]


def _convert_chart(file: OjnFile, difficulty: OjnDifficulty) -> Chart:
	events = sorted(difficulty.events, key=lambda event: (event.measure, event.position))

	time = 0.0
	bpm = file.bpm
	fraction = 1.0
	pointer = 0.0
	measure = 0
	meter = (4, 4)
	meter_dirty = False
	bpm_points: list[tuple[int, float, int, int]] = [(0, _canonical_bpm(bpm), 4, 4)]
	anchors = [0]
	notes: list[list] = []
	holds: dict[int, int] = {}

	for event in events:
		while event.measure > measure:
			if fraction - pointer < 0:
				raise UrcError(
					"syntax", event.offset, "measure fraction cuts before the current position"
				)
			time += (_MEASURE_MS * (fraction - pointer)) / bpm
			anchors.append(round_ms(time))
			if meter_dirty:
				bpm_points.append((round_ms(time), _canonical_bpm(bpm), 4, 4))
				meter = (4, 4)
				meter_dirty = False
			measure += 1
			fraction = 1.0
			pointer = 0.0
		time += (_MEASURE_MS * (event.position - pointer)) / bpm
		pointer = event.position

		if event.channel == 0:
			fraction = event.value
			approx = _fraction_meter(event.value)
			if approx is not None and approx != meter:
				meter = approx
				meter_dirty = True
				bpm_points.append((round_ms(time), _canonical_bpm(bpm), *meter))
		elif event.channel == 1:
			bpm_points.append((round_ms(time), _canonical_bpm(event.value), *meter))
			bpm = event.value
		else:
			_add_note(notes, holds, round_ms(time), event.channel - 2, event.type)

	for index in holds.values():
		notes[index][2] = NoteType.N

	first_note_time = min(
		(ms for ms, _lane, note_type in notes if note_type is not NoteType.LE), default=0
	)
	timing_points = build_timing(
		bpm_points=bpm_points,
		sv_points=[],
		first_note_time=first_note_time,
		source=".ojn",
		measure_anchor_ms=next((anchor for anchor in anchors if anchor >= first_note_time), None),
	)
	notes.sort(key=lambda note: (note[0], note[1], _TYPE_ORDER[note[2]]))
	final_notes = [
		Note(timestamp_ms=ms - first_note_time, lane=lane, type=note_type)
		for ms, lane, note_type in notes
	]
	check_hold_overlap(final_notes)

	return Chart(
		format_version=Version(major=1, minor=1),
		metadata=Metadata(
			original="O2Jam",
			title=file.title or "Unknown",
			artist=file.artist or "Unknown",
			creator=file.noter or "Unknown",
			version=_VERSIONS[difficulty.index],
		),
		judgment=None,
		layout=Layout(keys=7, special_keys=0, special_lanes=None),
		timing=timing_points,
		notes=final_notes,
	)


def _add_note(notes: list[list], holds: dict[int, int], ms: int, lane: int, kind: int) -> None:
	"""Append a note applying the client-emulating long-note repair."""
	if kind == 3:
		index = holds.pop(lane, None)
		if index is None:
			return
		if ms <= notes[index][0]:
			notes[index][2] = NoteType.N
		else:
			notes.append([ms, lane, NoteType.LE])
		return
	if lane in holds:
		return
	if kind == 2:
		holds[lane] = len(notes)
		notes.append([ms, lane, NoteType.LS])
	else:
		notes.append([ms, lane, NoteType.N])


def _canonical_bpm(value: float) -> float:
	"""Shortest decimal that round-trips the f32 BPM, widened to f64.

	Candidates are enumerated explicitly instead of relying on
	formatter-specific tie-breaking, so every language agrees.
	"""
	for precision in range(10):
		base = math.trunc(value * 10**precision)
		for candidate in (base, base + 1, base - 1):
			text = _decimal(candidate, precision)
			if _pack_f32(float(text)) == value:
				return float(text)
	return value


def _pack_f32(value: float) -> float:
	return struct.unpack("<f", struct.pack("<f", value))[0]


def _decimal(candidate: int, precision: int) -> str:
	sign = "-" if candidate < 0 else ""
	digits = str(abs(candidate))
	if precision == 0:
		return sign + digits
	digits = digits.rjust(precision + 1, "0")
	text = digits[:-precision] + "." + digits[-precision:]
	return sign + text.rstrip("0").rstrip(".")


def _fraction_meter(value: float) -> tuple[int, int] | None:
	"""Meter (beats, note_value) matching a measure fraction, if clean."""
	for note_value in range(1, 65):
		beats = round(value * note_value)
		if beats < 1:
			continue
		if abs(value - beats / note_value) <= _METER_TOLERANCE:
			divisor = math.gcd(beats, note_value)
			reduced = (beats // divisor, note_value // divisor)
			return None if reduced == (1, 1) else reduced
	return None
