"""BMS-family (.bms/.bme/.bml/.pms) source parser and converter."""

from dataclasses import dataclass, field
from itertools import groupby

from ..error import UrcError
from ..model import Chart, Layout, Metadata, Note, NoteType, Version
from ._shared import build_timing, check_hold_overlap, round_ms

_MEASURE_US = 240000000.0
_TYPE_ORDER = {NoteType.N: 0, NoteType.LS: 1, NoteType.LE: 2, NoteType.M: 3}

_SIDE_OF = {"1": 0, "5": 0, "D": 0, "2": 1, "6": 1, "E": 1}
_SYSTEM_CHANNELS = ("02", "03", "08", "09", "SC")
_LANE_TABLES: dict[tuple[str, int], dict[str, int]] = {
	("beat5", 0): {"1": 1, "2": 2, "3": 3, "4": 4, "5": 5, "6": 0},
	("beat5", 1): {"1": 7, "2": 8, "3": 9, "4": 10, "5": 11, "6": 6},
	("beat7", 0): {"1": 1, "2": 2, "3": 3, "4": 4, "5": 5, "6": 0, "8": 6, "9": 7},
	("beat7", 1): {"1": 9, "2": 10, "3": 11, "4": 12, "5": 13, "6": 8, "8": 14, "9": 15},
	("popn", 0): {"1": 0, "2": 1, "3": 2, "4": 3, "5": 4},
	("popn", 1): {"2": 5, "3": 6, "4": 7, "5": 8},
	("pms18", 0): {"1": 0, "2": 1, "3": 2, "4": 3, "5": 4, "6": 7, "7": 8, "8": 5, "9": 6},
	("pms18", 1): {"1": 9, "2": 10, "3": 11, "4": 12, "5": 13, "6": 16, "7": 17, "8": 14, "9": 15},
}
_TABLE_OF_MODE = {
	"5K": "beat5",
	"10K": "beat5",
	"7K": "beat7",
	"14K": "beat7",
	"PMS9": "popn",
	"PMS18": "pms18",
}
_LAYOUTS = {
	"5K": (5, 1, [0]),
	"7K": (7, 1, [0]),
	"10K": (10, 2, [0, 6]),
	"14K": (14, 2, [0, 8]),
	"PMS9": (9, 0, None),
	"PMS18": (18, 0, None),
}


@dataclass
class BmsChart:
	"""Source model of a BMS-family chart."""

	pms: bool
	base: int = 36
	title: str | None = None
	artist: str | None = None
	play_level: str | None = None
	bpm: float | None = None
	lntype: int = 1
	lnobj: str | None = None
	bpm_defs: dict[str, float] = field(default_factory=dict)
	stop_defs: dict[str, float] = field(default_factory=dict)
	scroll_defs: dict[str, float] = field(default_factory=dict)
	rates: dict[int, float] = field(default_factory=dict)
	measures: dict[int, dict[str, list[str]]] = field(default_factory=dict)


class _JavaRandom:
	"""java.util.Random-compatible 48-bit LCG (for the #RANDOM seed option)."""

	_MASK = (1 << 48) - 1
	_MULT = 0x5DEECE66D
	_ADD = 0xB

	def __init__(self, seed: int) -> None:
		self._state = (seed ^ self._MULT) & self._MASK

	def _next(self, bits: int) -> int:
		self._state = (self._state * self._MULT + self._ADD) & self._MASK
		return self._state >> (48 - bits)

	def next_int(self, bound: int) -> int:
		if bound & (-bound) == bound:
			return (bound * self._next(31)) >> 31
		while True:
			bits = self._next(31)
			value = bits % bound
			if bits - value + (bound - 1) <= 0x7FFFFFFF:
				return value


def parse_bms(
	data: bytes,
	*,
	pms: bool = False,
	seed: int | None = None,
	branches: list[int] | None = None,
) -> BmsChart:
	"""Parse BMS-family bytes into a source model."""
	text = _decode(data)
	return _parse(text, _scan_base(text), pms, seed, branches)


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

	mode = _detect_mode(chart.pms, used)

	bpm: float | None = None
	beats = 4
	time_us = 0.0
	prev_y = 0.0
	pending_stop = 0.0
	timed = [0.0] * len(objects)
	bpm_points: list[tuple[int, float, int]] = []
	sv_points: list[tuple[int, float]] = []

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
			else:
				timed[value] = time_us

		if (new_bpm, new_beats) != (bpm, beats):
			bpm_points.append((round_ms(time_us / 1000.0), new_bpm, new_beats))
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


def _parse(
	text: str,
	base: int,
	pms: bool,
	seed: int | None,
	branches: list[int] | None,
) -> BmsChart:
	chart = BmsChart(pms=pms, base=base)
	random_gen = _JavaRandom(seed) if seed is not None else None
	frames: list[list[bool]] = []
	random_value: int | None = None
	branch_index = 0

	for line_no, raw in enumerate(_lines(text), start=1):
		line = raw.strip()
		if not line.startswith("#"):
			continue
		head = line[1:]

		if len(head) >= 6 and head[:3].isdigit() and head[5] == ":":
			if not any(frame[0] for frame in frames):
				_add_message(chart, int(head[:3]), head[3:5], head[6:], line_no)
			continue

		command, _, argument = head.partition(" ")
		word = command.upper()

		if word == "RANDOM":
			count = _to_int(argument, line_no, "#RANDOM")
			if count < 1:
				raise UrcError("syntax", line_no, f"#RANDOM count must be >= 1: {count}")
			if branches is not None and branch_index < len(branches):
				pick = branches[branch_index]
				if not 1 <= pick <= count:
					raise UrcError("syntax", line_no, f"branch pick out of range: {pick}")
			elif random_gen is not None:
				pick = random_gen.next_int(count) + 1
			else:
				pick = 1
			random_value = pick
			branch_index += 1
		elif word == "IF":
			if random_value is None:
				raise UrcError("syntax", line_no, "unmatched #IF")
			condition = _to_int(argument, line_no, "#IF")
			frames.append([random_value != condition, random_value == condition])
		elif word == "ELSEIF":
			if not frames:
				raise UrcError("syntax", line_no, "unmatched #ELSEIF")
			condition = _to_int(argument, line_no, "#ELSEIF")
			matched = frames[-1][1] or random_value == condition
			frames[-1] = [not matched, matched]
		elif word == "ELSE":
			if not frames:
				raise UrcError("syntax", line_no, "unmatched #ELSE")
			frames[-1] = [frames[-1][1], True]
		elif word == "ENDIF":
			if not frames:
				raise UrcError("syntax", line_no, "unmatched #ENDIF")
			frames.pop()
		elif word in (
			"SETRANDOM",
			"ENDRANDOM",
			"SWITCH",
			"CASE",
			"SKIP",
			"DEF",
			"ENDSW",
			"SETSWITCH",
		):
			raise UrcError("unsupported-version", line_no, f"unsupported BMS command: #{command}")
		elif any(frame[0] for frame in frames):
			continue
		elif word == "BPM":
			chart.bpm = _to_float(argument, line_no, "#BPM")
		elif (len(command) == 5 and command[:3].upper() == "BPM") or (
			len(command) == 8 and command[:6].upper() == "EXBPM"
		):
			chart.bpm_defs[command[-2:]] = _to_float(argument, line_no, command)
		elif len(command) == 6 and command[:4].upper() == "STOP":
			chart.stop_defs[command[-2:]] = abs(_to_float(argument, line_no, command)) / 192.0
		elif len(command) == 8 and command[:6].upper() == "SCROLL":
			chart.scroll_defs[command[-2:]] = _to_float(argument, line_no, command)
		elif word == "LNTYPE":
			chart.lntype = _to_int(argument, line_no, "#LNTYPE")
			if chart.lntype not in (1, 2):
				raise UrcError("syntax", line_no, f"unsupported #LNTYPE: {chart.lntype}")
		elif word == "LNOBJ":
			chart.lnobj = argument.strip() or None
		elif word == "TITLE":
			chart.title = argument.strip() or None
		elif word == "ARTIST":
			chart.artist = argument.strip() or None
		elif word == "PLAYLEVEL":
			chart.play_level = argument.strip() or None

	if frames:
		raise UrcError("syntax", 1, "unterminated #IF block")
	return chart


def _add_message(
	chart: BmsChart, measure: int, channel: str, payload: str, line_no: int
) -> None:
	if channel == "02":
		rate = _to_float(payload, line_no, "measure length")
		if rate < 0:
			raise UrcError("syntax", line_no, f"negative measure length: {payload}")
		chart.rates[measure] = rate
		return
	if len(payload) % 2 != 0 or not all(_is_id_char(char) for char in payload):
		raise UrcError("syntax", line_no, f"malformed object list: {payload!r}")
	ids = [payload[index : index + 2] for index in range(0, len(payload), 2)]
	chart.measures.setdefault(measure, {}).setdefault(channel, []).extend(ids)


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
	table = _LANE_TABLES[(_TABLE_OF_MODE[mode], _SIDE_OF[channel[0]])]
	return table.get(channel[1])


def _id_value(text: str, base: int) -> int:
	def digit(char: str) -> int:
		if char.isdigit():
			return int(char)
		if "A" <= char <= "Z":
			return ord(char) - ord("A") + 10
		return ord(char) - ord("a") + 36

	return digit(text[0]) * base + digit(text[1])


def _decode(data: bytes) -> str:
	if data.startswith(b"\xef\xbb\xbf"):
		data = data[3:]
	for encoding in ("utf-8", "cp932"):
		try:
			return data.decode(encoding)
		except UnicodeDecodeError:
			continue
	raise UrcError("syntax", 1, "undecodable bytes: expected UTF-8 or Shift_JIS")


def _scan_base(text: str) -> int:
	for raw in _lines(text):
		parts = raw.strip().split()
		if len(parts) == 2 and parts[0].upper() == "#BASE":
			try:
				base = int(parts[1])
			except ValueError:
				raise UrcError("syntax", 1, f"invalid #BASE: {parts[1]}") from None
			if base not in (36, 62):
				raise UrcError("syntax", 1, f"unsupported #BASE: {base}")
			return base
	return 36


def _lines(text: str) -> list[str]:
	if text.startswith("﻿"):
		text = text[1:]
	return text.replace("\r\n", "\n").replace("\r", "\n").split("\n")


def _is_id_char(char: str) -> bool:
	return ("0" <= char <= "9") or ("A" <= char <= "Z") or ("a" <= char <= "z")


def _to_int(value: str, line_no: int, name: str) -> int:
	try:
		return int(value.strip())
	except ValueError:
		raise UrcError("syntax", line_no, f"invalid {name}: {value!r}") from None


def _to_float(value: str, line_no: int, name: str) -> float:
	try:
		return float(value.strip())
	except ValueError:
		raise UrcError("syntax", line_no, f"invalid {name}: {value!r}") from None
