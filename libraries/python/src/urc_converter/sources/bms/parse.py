"""Parser for BMS-family bytes."""

import math

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
	lines = _lines(_decode(data))
	return _parse(lines, _scan_base(lines), pms, seed, branches)


def _norm_id(token: str, base: int) -> str:
	return token.upper() if base == 36 else token


def _split_command(head: str) -> tuple[str, str]:
	for idx, ch in enumerate(head):
		if ch in (" ", "\t"):
			return head[:idx], head[idx + 1 :]
	return head, ""


def _parse(
	lines: list[str],
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

	for line_no, raw in enumerate(lines, start=1):
		line = raw.strip()
		if not line.startswith("#"):
			continue
		head = line[1:]

		if (
			len(head) >= 6
			and _is_ascii_digits(head[:3])
			and _is_id_char(head[3])
			and _is_id_char(head[4])
			and head[5] == ":"
		):
			if not any(frame[0] for frame in frames):
				_add_message(chart, int(head[:3]), head[3:5].upper(), head[6:], line_no)
			continue

		command, argument = _split_command(head)
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
			matched = random_value == condition
			frames.append([not matched, matched])
		elif word == "ELSEIF":
			if not frames:
				raise UrcError("syntax", line_no, "unmatched #ELSEIF")
			condition = _to_int(argument, line_no, "#ELSEIF")
			if frames[-1][1]:
				frames[-1] = [True, True]
			else:
				now = random_value == condition
				frames[-1] = [not now, now]
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
		elif (len(command) == 5 and word.startswith("BPM")) or (
			len(command) == 7 and word.startswith("EXBPM")
		):
			chart.bpm_defs[_norm_id(command[-2:], base)] = _to_float(argument, line_no, command)
		elif len(command) == 6 and word.startswith("STOP"):
			chart.stop_defs[_norm_id(command[-2:], base)] = (
				abs(_to_float(argument, line_no, command)) / 192.0
			)
		elif len(command) == 8 and word.startswith("SCROLL"):
			chart.scroll_defs[_norm_id(command[-2:], base)] = _to_float(argument, line_no, command)
		elif word == "LNTYPE":
			chart.lntype = _to_int(argument, line_no, "#LNTYPE")
			if chart.lntype not in (1, 2):
				raise UrcError("syntax", line_no, f"unsupported #LNTYPE: {chart.lntype}")
		elif word == "LNOBJ":
			raw_lnobj = argument.strip()
			chart.lnobj = _norm_id(raw_lnobj, base) if raw_lnobj else None
		elif word == "TITLE":
			chart.title = argument.strip() or None
		elif word == "ARTIST":
			chart.artist = argument.strip() or None
		elif word == "PLAYLEVEL":
			chart.play_level = argument.strip() or None

	if frames:
		raise UrcError("syntax", 1, "unterminated #IF block")
	return chart


def _is_relevant_channel(channel: str) -> bool:
	if channel in ("03", "08", "09", "SC"):
		return True
	return (
		len(channel) == 2
		and channel[0] in ("1", "2", "5", "6", "D", "E")
		and "1" <= channel[1] <= "9"
	)


def _add_message(
	chart: BmsChart, measure: int, channel: str, payload: str, line_no: int
) -> None:
	if channel == "02":
		rate = _to_float(payload, line_no, "measure length")
		if rate <= 0:
			raise UrcError("syntax", line_no, f"non-positive measure length: {payload}")
		chart.rates[measure] = rate
		return
	if len(payload) % 2 != 0 or not all(_is_id_char(char) for char in payload):
		raise UrcError("syntax", line_no, f"malformed object list: {payload!r}")
	if not payload or not _is_relevant_channel(channel):
		return
	if channel == "03" or chart.base == 36:
		normalized = payload.upper()
		ids = [normalized[index : index + 2] for index in range(0, len(normalized), 2)]
	else:
		ids = [payload[index : index + 2] for index in range(0, len(payload), 2)]
	chart.measures.setdefault(measure, {}).setdefault(channel, []).append(ids)


def _decode(data: bytes) -> str:
	if data.startswith(b"\xef\xbb\xbf"):
		data = data[3:]
	for encoding in ("utf-8", "cp932"):
		try:
			return data.decode(encoding)
		except UnicodeDecodeError:
			continue
	raise UrcError("syntax", 1, "undecodable bytes: expected UTF-8 or Shift_JIS")


def _scan_base(lines: list[str]) -> int:
	for raw in lines:
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
	text = text.lstrip("\ufeff")
	return text.replace("\r\n", "\n").replace("\r", "\n").split("\n")


def _is_ascii_digits(text: str) -> bool:
	return all("0" <= char <= "9" for char in text)


def _is_id_char(char: str) -> bool:
	return ("0" <= char <= "9") or ("A" <= char <= "Z") or ("a" <= char <= "z")


def _to_int(value: str, line_no: int, name: str) -> int:
	try:
		return int(value.strip())
	except ValueError:
		raise UrcError("syntax", line_no, f"invalid {name}: {value!r}") from None


def _to_float(value: str, line_no: int, name: str) -> float:
	try:
		parsed = float(value.strip())
	except ValueError:
		raise UrcError("syntax", line_no, f"invalid {name}: {value!r}") from None
	if not math.isfinite(parsed):
		raise UrcError("syntax", line_no, f"invalid {name}: {value!r}")
	return parsed
