"""Parser for StepMania (.sm/.ssc) simfiles."""

from ...error import UrcError
from .model import SmChart, SmFile, SmNote, Timing

_ROWS_PER_BEAT = 48

_STEP_LANES = {
	"dance-single": 4,
	"dance-double": 8,
	"dance-solo": 6,
	"dance-threepanel": 3,
	"pump-single": 5,
	"pump-halfdouble": 6,
	"pump-double": 10,
	"kb7-single": 7,
	"techno-single4": 4,
	"techno-single5": 5,
	"techno-single8": 8,
	"techno-double4": 8,
	"techno-double5": 10,
	"techno-double8": 16,
	"maniax-single": 4,
	"maniax-double": 8,
	"pnm-five": 5,
	"pnm-nine": 9,
	"para-single": 5,
	"ds3ddx-single": 8,
	"ez2-single": 5,
	"ez2-double": 10,
	"ez2-real": 7,
	"kickbox-human": 4,
	"kickbox-quadarm": 4,
	"kickbox-insect": 6,
	"kickbox-arachnid": 8,
}
_STEP_ALIASES = {"ez2-single-hard": "ez2-single", "para": "para-single"}

_TIMING_TAGS = frozenset(
	{"OFFSET", "BPMS", "STOPS", "FREEZES", "DELAYS", "WARPS", "SCROLLS", "FAKES", "TIMESIGNATURES"}
)


def resolve_lanes(steps_type: str) -> int:
	"""Track count for a steps type; unsupported types are an error."""
	name = _STEP_ALIASES.get(steps_type, steps_type)
	if name not in _STEP_LANES:
		raise UrcError(
			"unsupported-version",
			1,
			f"unsupported steps type: {steps_type or '(missing)'}",
		)
	return _STEP_LANES[name]


def parse_sm(text: str) -> SmFile:
	"""Parse a .sm or .ssc simfile into its source model."""
	simfile = SmFile()
	chart: SmChart | None = None

	for params in _tokenize(text):
		tag = params[0].upper()
		value = params[1] if len(params) > 1 else ""

		if tag == "NOTEDATA":
			chart = SmChart(steps_type="")
			continue
		if tag in ("NOTES", "NOTES2"):
			if chart is not None:
				chart.notes = _parse_note_data(value, resolve_lanes(chart.steps_type))
				simfile.charts.append(chart)
				chart = None
			elif len(params) >= 7:
				block = SmChart(
					steps_type=params[1].strip(),
					description=params[2].strip(),
					difficulty=params[3].strip(),
					credit=params[2].strip(),
				)
				block.notes = _parse_note_data(params[6], resolve_lanes(block.steps_type))
				simfile.charts.append(block)
			continue

		if chart is None:
			_song_tag(simfile, tag, value)
		else:
			_chart_tag(simfile, chart, tag, value)

	if not simfile.charts:
		raise UrcError("syntax", 1, "no chart in simfile")
	return simfile


def _song_tag(simfile: SmFile, tag: str, value: str) -> None:
	if tag == "TITLE":
		simfile.title = value
	elif tag == "SUBTITLE":
		simfile.subtitle = value
	elif tag == "ARTIST":
		simfile.artist = value
	elif tag == "CREDIT":
		simfile.credit = value
	else:
		_timing_tag(simfile.timing, tag, value)


def _chart_tag(simfile: SmFile, chart: SmChart, tag: str, value: str) -> None:
	if tag == "STEPSTYPE":
		chart.steps_type = value.strip()
	elif tag == "DESCRIPTION":
		chart.description = value.strip()
	elif tag == "DIFFICULTY":
		chart.difficulty = value.strip()
	elif tag == "CHARTNAME":
		chart.chartname = value.strip()
	elif tag == "CREDIT":
		chart.credit = value
	elif tag in _TIMING_TAGS:
		if chart.timing is None:
			chart.timing = Timing(offset=simfile.timing.offset)
		_timing_tag(chart.timing, tag, value)


def _timing_tag(timing: Timing, tag: str, value: str) -> None:
	if tag == "OFFSET":
		timing.offset = _float(value)
	elif tag == "BPMS":
		timing.bpms.extend(_pairs(value, skip_zero=True))
	elif tag in ("STOPS", "FREEZES"):
		timing.stops.extend(_pairs(value, skip_zero=True))
	elif tag == "DELAYS":
		timing.delays.extend(_pairs(value, skip_zero=True))
	elif tag == "WARPS":
		timing.warps.extend(_pairs(value))
	elif tag == "SCROLLS":
		timing.scrolls.extend(_pairs(value))
	elif tag == "FAKES":
		timing.fakes.extend(entry for entry in _pairs(value) if entry[1] > 0)
	elif tag == "TIMESIGNATURES":
		for parts in _expressions(value, minimum=3):
			beat, num, den = _beat(parts[0]), _int(parts[1]), _int(parts[2])
			if num >= 1 and den >= 1 and beat >= 0:
				timing.timesigs.append((beat, num, den))


def _tokenize(text: str) -> list[list[str]]:
	"""Split a simfile into MSD values (#TAG:param:...;) following MsdFile."""
	values: list[list[str]] = []
	params: list[str] = []
	current: list[str] = []
	line: list[str] = []
	reading = False

	def end_param() -> None:
		params.append("".join(current))
		current.clear()
		line.clear()

	i = 0
	n = len(text)
	while i < n:
		if i + 1 < n and text[i] == "/" and text[i + 1] == "/":
			while i < n and text[i] != "\n":
				i += 1
			continue
		if reading and text[i] == "#":
			if "".join(line).strip(" \t"):
				current.append("#")
				line.append("#")
				i += 1
				continue
			params.append("".join(current).rstrip(" \t\r\n"))
			values.append(params)
			params = []
			current = []
			line = []
			reading = False
			continue
		if not reading:
			if text[i] == "#":
				reading = True
				line.clear()
			elif text[i] != "\\":
				i += 1
				continue
			elif i + 1 < n:
				i += 2
				continue
			i += 1
			continue
		if text[i] == ":":
			end_param()
		elif text[i] == ";":
			end_param()
			values.append(params)
			params = []
			current = []
			line = []
			reading = False
		elif text[i] == "\\":
			i += 1
			if i < n:
				current.append(text[i])
				line.append(text[i])
		else:
			current.append(text[i])
			line.append(text[i])
		if i < n and text[i] in "\r\n":
			line.clear()
		i += 1

	if reading:
		params.append("".join(current))
		values.append(params)
	return values


def _expressions(value: str, minimum: int) -> list[list[str]]:
	parts: list[list[str]] = []
	for expression in value.split(","):
		if not expression.strip():
			continue
		fields = expression.split("=")
		if len(fields) < minimum:
			raise UrcError("syntax", 1, f"malformed timing expression: {expression}")
		parts.append(fields)
	return parts


def _pairs(value: str, skip_zero: bool = False) -> list[tuple[float, float]]:
	pairs = []
	for parts in _expressions(value, minimum=2):
		if len(parts) != 2:
			raise UrcError("syntax", 1, f"malformed timing expression: {'='.join(parts)}")
		entry = (_beat(parts[0]), _float(parts[1]))
		if not skip_zero or entry[1] != 0:
			pairs.append(entry)
	return pairs


def _beat(token: str) -> float:
	if token.rstrip().endswith(("r", "R")):
		raise UrcError("syntax", 1, f"row-format beats are not supported: {token}")
	return _float(token)


def _float(token: str) -> float:
	try:
		return float(token.strip())
	except ValueError:
		raise UrcError("syntax", 1, f"invalid number: {token}") from None


def _int(token: str) -> int:
	try:
		return int(token.strip())
	except ValueError:
		raise UrcError("syntax", 1, f"invalid integer: {token}") from None


def _parse_note_data(data: str, lanes: int) -> list[SmNote]:
	notes: list[SmNote] = []
	open_holds: dict[int, SmNote] = {}

	measure = 0
	for part in data.split(","):
		if not part:
			continue
		content = [line for line in (raw.strip(" \t\r") for raw in part.split("\n")) if line]
		for index, line in enumerate(content):
			row = _row((measure + index / len(content)) * 4.0)
			track = 0
			position = 0
			while track < lanes and position < len(line):
				char = line[position]
				position += 1
				if char == "1":
					notes.append(SmNote(row=row, track=track, kind="tap"))
				elif char in ("2", "4"):
					if track in open_holds:
						raise UrcError("syntax", 1, f"overlapping hold head at row {row}")
					note = SmNote(row=row, track=track, kind="hold" if char == "2" else "roll")
					notes.append(note)
					open_holds[track] = note
				elif char == "3":
					if track not in open_holds:
						raise UrcError("syntax", 1, f"hold tail without a head at row {row}")
					open_holds.pop(track).tail_row = row
				elif char == "M":
					notes.append(SmNote(row=row, track=track, kind="mine"))
				elif char == "L":
					notes.append(SmNote(row=row, track=track, kind="lift"))
				elif char == "F":
					notes.append(SmNote(row=row, track=track, kind="fake"))
				if position < len(line) and line[position] == "[":
					end = line.find("]", position)
					position = len(line) if end < 0 else end + 1
				track += 1
		measure += 1

	if open_holds:
		raise UrcError("syntax", 1, "hold note without a tail")
	return notes


def _row(beat: float) -> int:
	value = beat * _ROWS_PER_BEAT
	return int(value + 0.5) if value >= 0 else int(value - 0.5)
