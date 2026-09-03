"""Parser for .qua YAML text."""

import yaml

from ...error import UrcError
from .._shared import round_ms
from .model import QuaHitObject, QuaMap, QuaSvPoint, QuaTimingPoint


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
