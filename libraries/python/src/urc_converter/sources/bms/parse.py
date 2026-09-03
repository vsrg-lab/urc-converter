"""Parser for BMS-family bytes."""

from ...error import UrcError
from .model import BmsChart


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
