"""Parser for .osu text."""

from ...error import UrcError
from .model import OsuBeatmap, OsuHitObject, OsuTimingPoint


def parse_osu(text: str) -> OsuBeatmap:
	"""Parse .osu text into a source model."""
	beatmap = OsuBeatmap()
	section = None

	for line_no, raw in enumerate(_lines(text), start=1):
		line = raw.strip()

		if line.startswith("[") and line.endswith("]"):
			section = line[1:-1]
			continue

		if line == "" or line.startswith("//") or section is None:
			continue

		if section == "General":
			_general(beatmap, line, line_no)
		elif section == "Metadata":
			_metadata(beatmap, line)
		elif section == "Difficulty":
			_difficulty(beatmap, line, line_no)
		elif section == "TimingPoints":
			beatmap.timing_points.append(_timing_point(line, line_no))
		elif section == "HitObjects":
			beatmap.hit_objects.append(_hit_object(line, line_no))

	return beatmap


def _lines(text: str) -> list[str]:
	if text.startswith("\ufeff"):
		text = text[1:]
	return text.replace("\r\n", "\n").replace("\r", "\n").split("\n")


def _general(beatmap: OsuBeatmap, line: str, line_no: int) -> None:
	key, _, value = line.partition(":")
	if key.strip() == "Mode":
		beatmap.mode = _to_int(value.strip(), line_no, "Mode")


def _metadata(beatmap: OsuBeatmap, line: str) -> None:
	key, _, value = line.partition(":")
	attribute = {
		"Title": "title",
		"TitleUnicode": "title_unicode",
		"Artist": "artist",
		"ArtistUnicode": "artist_unicode",
		"Creator": "creator",
		"Version": "version",
	}.get(key.strip())
	if attribute is not None:
		setattr(beatmap, attribute, value.strip())


def _difficulty(beatmap: OsuBeatmap, line: str, line_no: int) -> None:
	key, _, value = line.partition(":")
	name = key.strip()

	if name == "CircleSize":
		beatmap.circle_size = _to_float(value.strip(), line_no, "CircleSize")
	elif name == "OverallDifficulty":
		beatmap.overall_difficulty = _to_float(value.strip(), line_no, "OverallDifficulty")


def _timing_point(line: str, line_no: int) -> OsuTimingPoint:
	fields = [field.strip() for field in line.split(",")]
	if len(fields) < 2:
		raise UrcError("syntax", line_no, f"timing point needs at least 2 fields: {line!r}")

	return OsuTimingPoint(
		time=_to_int(fields[0], line_no, "timing time"),
		beat_length=_to_float(fields[1], line_no, "beat length"),
		meter=_to_int(fields[2], line_no, "meter") if len(fields) > 2 and fields[2] else 4,
		uninherited=bool(int(fields[6])) if len(fields) > 6 and fields[6] else True,
	)


def _hit_object(line: str, line_no: int) -> OsuHitObject:
	fields = [field.strip() for field in line.split(",")]
	if len(fields) < 5:
		raise UrcError("syntax", line_no, f"hit object needs at least 5 fields: {line!r}")

	x = _to_int(fields[0], line_no, "hit object x")
	time = _to_int(fields[2], line_no, "hit object time")
	type_bits = _to_int(fields[3], line_no, "hit object type")

	is_hold = bool(type_bits & 128)
	if not is_hold and not type_bits & 1:
		raise UrcError("syntax", line_no, f"unsupported hit object type: {type_bits}")

	end_time = None
	if is_hold:
		if len(fields) < 6:
			raise UrcError("syntax", line_no, f"hold note needs an end time: {line!r}")
		end_time = _to_int(fields[5].split(":", 1)[0], line_no, "hold end time")

	return OsuHitObject(x=x, time=time, is_hold=is_hold, end_time=end_time)


def _to_int(token: str, line_no: int, label: str) -> int:
	try:
		return round(float(token))
	except ValueError:
		raise UrcError("syntax", line_no, f"invalid {label}: {token!r}") from None


def _to_float(token: str, line_no: int, label: str) -> float:
	try:
		return float(token)
	except ValueError:
		raise UrcError("syntax", line_no, f"invalid {label}: {token!r}") from None
