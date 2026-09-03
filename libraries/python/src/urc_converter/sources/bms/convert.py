"""Mapper from the BMS-family source model onto a URC chart."""

from itertools import groupby

from ...error import UrcError
from ...model import Chart, Layout, Metadata, Note, NoteType, Version
from .._shared import build_timing, check_hold_overlap, round_ms
from .model import BmsChart

_MEASURE_US = 240000000.0
_TYPE_ORDER = {NoteType.N: 0, NoteType.LS: 1, NoteType.LE: 2, NoteType.M: 3}

_SIDE_OF = {"1": 0, "5": 0, "D": 0, "2": 1, "6": 1, "E": 1}
_SYSTEM_CHANNELS = ("02", "03", "08", "09", "SC")
_LAYOUTS = {
	"5K": (5, 1, [0]),
	"7K": (7, 1, [0]),
	"10K": (10, 2, [0, 6]),
	"14K": (14, 2, [0, 8]),
	"PMS9": (9, 0, None),
	"PMS18": (18, 0, None),
}


def convert_bms(chart: BmsChart) -> Chart:
	"""Map a BMS-family chart onto a URC chart."""
	if chart.bpm is None or chart.bpm <= 0:
		raise UrcError("syntax", 1, "missing or non-positive #BPM")

	max_measure = max(chart.measures, default=-1)
	boundaries = [0.0]
	for measure in range(max_measure + 1):
		boundaries.append(boundaries[-1] + chart.rates.get(measure, 1.0))

	entries: list[tuple[float, int, str, float | int]] = [(0.0, 0, "bpm", chart.bpm)]
	objects: list[tuple[float, str, str]] = []
	used: set[tuple[int, str]] = set()

	for measure in range(max_measure + 1):
		rate = chart.rates.get(measure, 1.0)
		if rate != chart.rates.get(measure - 1, 1.0):
			beats = rate * 4.0
			if abs(beats - round(beats)) < 1e-9 and round(beats) >= 1:
				entries.append((boundaries[measure], 3, "meter", round(beats)))

		for channel, ids in chart.measures.get(measure, {}).items():
			for index, obj in enumerate(ids):
				y = boundaries[measure] + (index / len(ids)) * rate

				if channel in _SYSTEM_CHANNELS:
					if obj == "00":
						continue
					if channel == "03":
						digits = _id_value(obj, chart.base)
						entries.append((y, 0, "bpm", float((digits // 36) * 16 + digits % 36)))
					elif channel == "08":
						if obj not in chart.bpm_defs:
							raise UrcError("syntax", 1, f"undefined #BPM{obj}")
						entries.append((y, 0, "bpm", chart.bpm_defs[obj]))
					elif channel == "09":
						if obj not in chart.stop_defs:
							raise UrcError("syntax", 1, f"undefined #STOP{obj}")
						entries.append((y, 1, "stop", chart.stop_defs[obj]))
					else:
						if obj not in chart.scroll_defs:
							raise UrcError("syntax", 1, f"undefined #SCROLL{obj}")
						entries.append((y, 2, "scroll", chart.scroll_defs[obj]))
					continue

				kind = _channel_kind(channel)
				if kind is None:
					continue
				if obj != "00":
					used.add((_SIDE_OF[channel[0]], channel[1]))
				if obj == "00" and kind != "ln":
					continue
				objects.append((y, channel, obj))
				entries.append((y, 4, "object", len(objects) - 1))

	entries.extend((y, 5, "anchor", 0) for y in boundaries)

	mode = _detect_mode(chart.pms, used)

	bpm: float | None = None
	beats = 4
	time_us = 0.0
	prev_y = 0.0
	pending_stop = 0.0
	timed = [0.0] * len(objects)
	bpm_points: list[tuple[int, float, int, int]] = []
	sv_points: list[tuple[int, float]] = []
	anchors: list[int] = []

	entries.sort(key=lambda entry: (entry[0], entry[1]))
	for y, group in groupby(entries, key=lambda entry: entry[0]):
		if bpm is not None:
			time_us += _MEASURE_US * (y - prev_y) / bpm
		time_us += pending_stop
		pending_stop = 0.0

		new_bpm, new_beats, scroll = bpm, beats, None
		for _, _priority, kind, value in group:
			if kind == "bpm":
				new_bpm = value
			elif kind == "meter":
				new_beats = value
			elif kind == "stop":
				pending_stop = _MEASURE_US * value / new_bpm
			elif kind == "scroll":
				scroll = value
			elif kind == "anchor":
				anchors.append(round_ms(time_us / 1000.0))
			else:
				timed[value] = time_us

		if (new_bpm, new_beats) != (bpm, beats):
			bpm_points.append((round_ms(time_us / 1000.0), new_bpm, new_beats, 4))
		if scroll is not None:
			sv_points.append((round_ms(time_us / 1000.0), scroll))
		bpm, beats = new_bpm, new_beats
		prev_y = y

	notes = _build_notes(chart, mode, objects, timed)
	first_note_time = min(
		(time for time, _lane, note_type in notes if note_type is not NoteType.LE),
		default=0,
	)

	timing = build_timing(
		bpm_points=bpm_points,
		sv_points=sv_points,
		first_note_time=first_note_time,
		source=".bms",
		measure_anchor_ms=next((time for time in anchors if time >= first_note_time), None),
	)

	urc_notes = sorted(
		(
			Note(timestamp_ms=time - first_note_time, lane=lane, type=note_type)
			for time, lane, note_type in notes
		),
		key=lambda note: (note.timestamp_ms, note.lane, _TYPE_ORDER[note.type]),
	)
	check_hold_overlap(urc_notes)

	keys, special_keys, special_lanes = _LAYOUTS[mode]
	return Chart(
		format_version=Version(major=1, minor=1),
		metadata=Metadata(
			original="PMS" if chart.pms else "BMS",
			title=chart.title or "Unknown",
			artist=chart.artist or "Unknown",
			creator="Unknown",
			version=chart.play_level or "Unknown",
		),
		judgment=None,
		layout=Layout(keys=keys, special_keys=special_keys, special_lanes=special_lanes),
		timing=timing,
		notes=urc_notes,
	)


def _build_notes(
	chart: BmsChart,
	mode: str,
	objects: list[tuple[float, str, str]],
	timed: list[float],
) -> list[tuple[int, int, NoteType]]:
	streams: dict[str, list[tuple[float, str]]] = {}
	for index, (_y, channel, obj) in enumerate(objects):
		streams.setdefault(channel, []).append((timed[index], obj))

	notes: list[tuple[int, int, NoteType]] = []
	for channel, stream in streams.items():
		lane = _lane(mode, channel)
		if lane is None:
			continue
		kind = _channel_kind(channel)

		if kind == "mine":
			notes.extend((round_ms(time / 1000.0), lane, NoteType.M) for time, _obj in stream)
		elif kind == "ln":
			_pair_long_notes(chart, stream, lane, notes)
		else:
			pending: float | None = None
			for time, obj in stream:
				if chart.lnobj is not None and obj == chart.lnobj and pending is not None:
					notes.append((round_ms(pending / 1000.0), lane, NoteType.LS))
					notes.append((round_ms(time / 1000.0), lane, NoteType.LE))
					pending = None
				else:
					if pending is not None:
						notes.append((round_ms(pending / 1000.0), lane, NoteType.N))
					pending = time
			if pending is not None:
				notes.append((round_ms(pending / 1000.0), lane, NoteType.N))
	return notes


def _pair_long_notes(
	chart: BmsChart,
	stream: list[tuple[float, str]],
	lane: int,
	notes: list[tuple[int, int, NoteType]],
) -> None:
	start: float | None = None
	if chart.lntype == 1:
		for time, obj in stream:
			if obj == "00":
				continue
			if start is None:
				start = time
			else:
				notes.append((round_ms(start / 1000.0), lane, NoteType.LS))
				notes.append((round_ms(time / 1000.0), lane, NoteType.LE))
				start = None
	else:
		for time, obj in stream:
			if obj == "00":
				if start is not None:
					notes.append((round_ms(start / 1000.0), lane, NoteType.LS))
					notes.append((round_ms(time / 1000.0), lane, NoteType.LE))
					start = None
			elif start is None:
				start = time
	if start is not None:
		raise UrcError("syntax", 1, f"long note on lane {lane} has no end")


def _channel_kind(channel: str) -> str | None:
	if len(channel) != 2:
		return None
	first, second = channel
	if first in ("1", "2") and "1" <= second <= "9":
		return "visible"
	if first in ("5", "6") and "1" <= second <= "9":
		return "ln"
	if first in ("D", "E") and "1" <= second <= "9":
		return "mine"
	return None


def _detect_mode(pms: bool, used: set[tuple[int, str]]) -> str:
	if pms:
		if any(
			second in ("6", "7", "8", "9") or (side == 1 and second == "1")
			for side, second in used
		):
			return "PMS18"
		return "PMS9"
	seven = any(second in ("8", "9") for _side, second in used)
	double = any(side == 1 for side, _second in used)
	if seven and double:
		return "14K"
	if double:
		return "10K"
	if seven:
		return "7K"
	return "5K"


def _lane(mode: str, channel: str) -> int | None:
	side = _SIDE_OF[channel[0]]
	key = channel[1]
	if mode in ("5K", "10K"):
		if key == "6":
			return side * 6
		if "1" <= key <= "5":
			return int(key) + side * 6
		return None
	if mode in ("7K", "14K"):
		if key == "6":
			return side * 8
		if "1" <= key <= "5":
			return int(key) + side * 8
		if key in ("8", "9"):
			return int(key) - 8 + 6 + side * 8
		return None
	if mode == "PMS9":
		if side == 0 and "1" <= key <= "5":
			return int(key) - 1
		if side == 1 and "2" <= key <= "5":
			return int(key) - 2 + 5
		return None
	if mode == "PMS18":
		base = side * 9
		if "1" <= key <= "5":
			return base + int(key) - 1
		match key:
			case "8":
				return base + 5
			case "9":
				return base + 6
			case "6":
				return base + 7
			case "7":
				return base + 8
		return None
	return None


def _id_value(text: str, base: int) -> int:
	def digit(char: str) -> int:
		if char.isdigit():
			return int(char)
		if "A" <= char <= "Z":
			return ord(char) - ord("A") + 10
		return ord(char) - ord("a") + 36

	return digit(text[0]) * base + digit(text[1])
