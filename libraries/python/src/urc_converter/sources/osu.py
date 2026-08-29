"""osu!mania (.osu) source parser and converter."""

import math
from dataclasses import dataclass, field

from ..error import UrcError
from ..model import Chart, Judgment, Layout, Metadata, Note, NoteType, Version
from ._shared import build_timing, check_hold_overlap

_JUDGMENT_RATES = [100.0, 100.0, 66.67, 33.33, 16.67, 0.0]
_KEY_MIN, _KEY_MAX = 1, 18


@dataclass
class OsuTimingPoint:
	"""One [TimingPoints] entry, reduced to the fields we map."""

	time: int
	beat_length: float
	meter: int
	uninherited: bool


@dataclass
class OsuHitObject:
	"""One [HitObjects] entry, reduced to the fields we map."""

	x: int
	time: int
	is_hold: bool
	end_time: int | None = None


@dataclass
class OsuBeatmap:
	"""Source model of an osu!mania beatmap."""

	mode: int = 3
	title: str | None = None
	title_unicode: str | None = None
	artist: str | None = None
	artist_unicode: str | None = None
	creator: str | None = None
	version: str | None = None
	circle_size: float | None = None
	overall_difficulty: float | None = None
	timing_points: list[OsuTimingPoint] = field(default_factory=list)
	hit_objects: list[OsuHitObject] = field(default_factory=list)


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


def convert_osu(beatmap: OsuBeatmap) -> Chart:
	"""Map an osu!mania beatmap onto a URC chart."""
	if beatmap.mode != 3:
		raise UrcError("unsupported-version", 1, f"unsupported game mode: {beatmap.mode}")

	if beatmap.circle_size is None:
		raise UrcError("syntax", 1, "missing CircleSize")

	if beatmap.circle_size != int(beatmap.circle_size):
		raise UrcError("syntax", 1, f"CircleSize must be an integer: {beatmap.circle_size}")

	keys = int(beatmap.circle_size)
	if not _KEY_MIN <= keys <= _KEY_MAX:
		raise UrcError("syntax", 1, f"CircleSize out of range: {keys}")

	if any(point.beat_length == 0 for point in beatmap.timing_points):
		raise UrcError("syntax", 1, "timing point with zero beat length")

	first_note_time = min((obj.time for obj in beatmap.hit_objects), default=0)

	timing = build_timing(
		bpm_points=[
			(point.time, 60000.0 / point.beat_length, point.meter)
			for point in beatmap.timing_points
			if point.uninherited
		],
		sv_points=[
			(point.time, -100.0 / point.beat_length)
			for point in beatmap.timing_points
			if not point.uninherited
		],
		first_note_time=first_note_time,
		source=".osu",
	)

	notes: list[Note] = []
	for obj in beatmap.hit_objects:
		lane = min(max(math.floor(obj.x * keys / 512), 0), keys - 1)

		if obj.is_hold:
			if obj.end_time < obj.time:
				raise UrcError(
					"syntax", 1, f"hold ends before it starts: {obj.end_time} < {obj.time}"
				)
			notes.append(Note(timestamp_ms=obj.time - first_note_time, lane=lane, type=NoteType.LS))
			notes.append(
				Note(timestamp_ms=obj.end_time - first_note_time, lane=lane, type=NoteType.LE)
			)
		else:
			notes.append(Note(timestamp_ms=obj.time - first_note_time, lane=lane, type=NoteType.N))

	check_hold_overlap(notes)

	judgment = None
	if beatmap.overall_difficulty is not None:
		od = beatmap.overall_difficulty
		windows = [16.5] + [base - 3 * od + 0.5 for base in (64, 97, 127, 151, 188)]
		judgment = Judgment(windows=windows, rates=list(_JUDGMENT_RATES))

	title = beatmap.title_unicode or beatmap.title
	artist = beatmap.artist_unicode or beatmap.artist
	missing = [
		label
		for label, value in (
			("Title", title),
			("Artist", artist),
			("Creator", beatmap.creator),
			("Version", beatmap.version),
		)
		if not value
	]
	if missing:
		raise UrcError("syntax", 1, f"missing metadata: {', '.join(missing)}")

	return Chart(
		format_version=Version(major=1, minor=1),
		metadata=Metadata(
			original="osu!mania",
			title=title,
			artist=artist,
			creator=beatmap.creator,
			version=beatmap.version,
		),
		judgment=judgment,
		layout=Layout(keys=keys, special_keys=0, special_lanes=None),
		timing=timing,
		notes=notes,
	)


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
