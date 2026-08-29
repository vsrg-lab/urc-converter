"""Quaver (.qua) source parser and converter."""

from dataclasses import dataclass, field

import yaml

from ..error import UrcError
from ..model import Chart, Layout, Metadata, Note, NoteType, Version
from ._shared import build_timing, check_hold_overlap, round_ms

_MODE_KEYS = {1: 4, 2: 7, 3: 1, 4: 2, 5: 3, 6: 5, 7: 6, 8: 8, 9: 9, 10: 10}


@dataclass
class QuaTimingPoint:
	"""One TimingPoints entry."""

	start_time: int
	bpm: float
	signature: int


@dataclass
class QuaSvPoint:
	"""One ScrollSpeedFactors entry."""

	start_time: int
	multiplier: float


@dataclass
class QuaHitObject:
	"""One HitObjects entry."""

	start_time: int
	lane: int
	end_time: int = 0
	mine: bool = False


@dataclass
class QuaMap:
	"""Source model of a .qua chart."""

	mode: int = 1
	has_scratch_key: bool = False
	title: str | None = None
	artist: str | None = None
	creator: str | None = None
	difficulty_name: str | None = None
	timing_points: list[QuaTimingPoint] = field(default_factory=list)
	sv_points: list[QuaSvPoint] = field(default_factory=list)
	hit_objects: list[QuaHitObject] = field(default_factory=list)


def parse_qua(text: str) -> QuaMap:
	"""Parse .qua YAML text into a source model."""
	try:
		data = yaml.safe_load(text)
	except yaml.YAMLError as error:
		raise UrcError("syntax", _yaml_line(error), f"invalid YAML: {error}") from None

	if not isinstance(data, dict):
		raise UrcError("syntax", 1, ".qua must be a YAML mapping")

	qua = QuaMap(
		mode=_int(data, "Mode", 1),
		has_scratch_key=bool(data.get("HasScratchKey", False)),
		title=_str(data, "Title"),
		artist=_str(data, "Artist"),
		creator=_str(data, "Creator"),
		difficulty_name=_str(data, "DifficultyName"),
	)

	for entry in _entries(data, "TimingPoints"):
		signature = _int(entry, "Signature", 4)
		if signature == 0:  # legacy unset value, restored to 4/4 by the game
			signature = 4
		if signature not in (3, 4):
			raise UrcError("syntax", 1, f"unsupported time signature: {signature}")

		qua.timing_points.append(
			QuaTimingPoint(
				start_time=round_ms(_float(entry, "StartTime")),
				bpm=_float(entry, "Bpm"),
				signature=signature,
			)
		)

	for entry in _entries(data, "ScrollSpeedFactors"):
		qua.sv_points.append(
			QuaSvPoint(
				start_time=round_ms(_float(entry, "StartTime")),
				multiplier=_float(entry, "Multiplier"),
			)
		)

	for entry in _entries(data, "HitObjects"):
		qua.hit_objects.append(
			QuaHitObject(
				start_time=_int(entry, "StartTime", -1),
				lane=_int(entry, "Lane", 0),
				end_time=int(entry.get("EndTime") or 0),
				mine=_int(entry, "Type", 0) == 1,
			)
		)

	return qua


def convert_qua(qua: QuaMap) -> Chart:
	"""Map a Quaver chart onto a URC chart."""
	keys = _MODE_KEYS.get(qua.mode)
	if keys is None:
		raise UrcError("unsupported-version", 1, f"unsupported Quaver mode: {qua.mode}")

	special_keys = 1 if qua.has_scratch_key else 0

	first_note_time = min((obj.start_time for obj in qua.hit_objects), default=0)

	timing = build_timing(
		bpm_points=[(point.start_time, point.bpm, point.signature) for point in qua.timing_points],
		sv_points=[(point.start_time, point.multiplier) for point in qua.sv_points],
		first_note_time=first_note_time,
		source=".qua",
	)

	total = keys + special_keys
	notes: list[Note] = []

	for obj in qua.hit_objects:
		lane = obj.lane - 1
		if not 0 <= lane < total:
			raise UrcError("syntax", 1, f"lane out of range: {obj.lane}")

		if obj.end_time and obj.end_time < obj.start_time:
			raise UrcError(
				"syntax", 1, f"hold ends before it starts: {obj.end_time} < {obj.start_time}"
			)

		if obj.end_time:
			notes.append(
				Note(timestamp_ms=obj.start_time - first_note_time, lane=lane, type=NoteType.LS)
			)
			notes.append(
				Note(timestamp_ms=obj.end_time - first_note_time, lane=lane, type=NoteType.LE)
			)
		elif obj.mine:
			notes.append(
				Note(timestamp_ms=obj.start_time - first_note_time, lane=lane, type=NoteType.M)
			)
		else:
			notes.append(
				Note(timestamp_ms=obj.start_time - first_note_time, lane=lane, type=NoteType.N)
			)

	check_hold_overlap(notes)

	missing = [
		label
		for label, value in (
			("Title", qua.title),
			("Artist", qua.artist),
			("Creator", qua.creator),
			("DifficultyName", qua.difficulty_name),
		)
		if not value
	]
	if missing:
		raise UrcError("syntax", 1, f"missing metadata: {', '.join(missing)}")

	return Chart(
		format_version=Version(major=1, minor=1),
		metadata=Metadata(
			original="Quaver",
			title=qua.title,
			artist=qua.artist,
			creator=qua.creator,
			version=qua.difficulty_name,
		),
		judgment=None,
		layout=Layout(
			keys=keys,
			special_keys=special_keys,
			special_lanes=[keys] if qua.has_scratch_key else None,
		),
		timing=timing,
		notes=notes,
	)


def _yaml_line(error: yaml.YAMLError) -> int:
	mark = getattr(error, "problem_mark", None)
	return mark.line + 1 if mark else 1


def _entries(data: dict, key: str) -> list[dict]:
	value = data.get(key)
	if value is None:
		return []
	if not isinstance(value, list) or any(not isinstance(entry, dict) for entry in value):
		raise UrcError("syntax", 1, f"{key} must be a list of mappings")
	return value


def _str(data: dict, key: str) -> str | None:
	value = data.get(key)
	return str(value) if value is not None else None


def _int(data: dict, key: str, default: int) -> int:
	value = data.get(key)
	if value is None:
		return default
	try:
		return int(value)
	except TypeError, ValueError:
		raise UrcError("syntax", 1, f"invalid {key}: {value!r}") from None


def _float(entry: dict, key: str) -> float:
	value = entry.get(key)
	if value is None:
		raise UrcError("syntax", 1, f"missing {key}")
	try:
		return float(value)
	except TypeError, ValueError:
		raise UrcError("syntax", 1, f"invalid {key}: {value!r}") from None
