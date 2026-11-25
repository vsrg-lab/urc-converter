"""Parser for .qua YAML text."""

import math
from collections.abc import Iterator

import yaml

from ...error import UrcError
from .._shared import round_ms
from .model import QuaHitObject, QuaMap, QuaSvPoint, QuaTimingPoint

_LOADER = getattr(yaml, "CSafeLoader", yaml.SafeLoader)

_MODE_NAMES = {
	"keys4": 1,
	"keys7": 2,
	"keys1": 3,
	"keys2": 4,
	"keys3": 5,
	"keys5": 6,
	"keys6": 7,
	"keys8": 8,
	"keys9": 9,
	"keys10": 10,
}

_SIGNATURE_NAMES = {"quadruple": 4, "triple": 3}
_HIT_OBJECT_TYPES = {"normal": 0, "mine": 1}
_NULL_TAG = "tag:yaml.org,2002:null"
_NULL_VALUES = frozenset({"", "~", "null", "Null", "NULL"})


class _EventStream:
	def __init__(self, text: str) -> None:
		try:
			self._events: Iterator[yaml.Event] = yaml.parse(text, Loader=_LOADER)
		except yaml.YAMLError as error:
			raise UrcError("syntax", _yaml_line(error), f"invalid YAML: {error}") from None

	def next(self) -> yaml.Event:
		try:
			return next(self._events)
		except yaml.YAMLError as error:
			raise UrcError("syntax", _yaml_line(error), f"invalid YAML: {error}") from None
		except StopIteration:
			raise UrcError("syntax", 1, "unexpected end of YAML stream") from None


def parse_qua(text: str) -> QuaMap:
	"""Parse .qua YAML text into a source model."""
	cleaned = text.lstrip("\ufeff")
	stream = _EventStream(cleaned)

	# StreamStartEvent -> DocumentStartEvent (or StreamEndEvent for empty input)
	stream.next()
	doc_event = stream.next()
	if isinstance(doc_event, yaml.StreamEndEvent):
		raise UrcError("syntax", 1, ".qua must be a YAML mapping")

	root_event = stream.next()
	if not isinstance(root_event, yaml.MappingStartEvent):
		raise UrcError("syntax", 1, ".qua must be a YAML mapping")

	qua = QuaMap()
	while True:
		key_event = stream.next()
		if isinstance(key_event, yaml.MappingEndEvent):
			break
		if not isinstance(key_event, yaml.ScalarEvent):
			_skip_value(stream, key_event)
			_skip_value(stream, stream.next())
			continue

		key = key_event.value
		val_event = stream.next()
		if key == "Mode":
			qua.mode = _parse_mode(val_event)
		elif key == "HasScratchKey":
			qua.has_scratch_key = _parse_bool(val_event, key, False)
		elif key == "BPMDoesNotAffectScrollVelocity":
			qua.bpm_does_not_affect_sv = _parse_bool(val_event, key, False)
		elif key == "InitialScrollVelocity":
			qua.initial_scroll_velocity = _parse_float(val_event, key, 0.0)
		elif key == "Title":
			qua.title = _parse_str(val_event, key)
		elif key == "Artist":
			qua.artist = _parse_str(val_event, key)
		elif key == "Creator":
			qua.creator = _parse_str(val_event, key)
		elif key == "DifficultyName":
			qua.difficulty_name = _parse_str(val_event, key)
		elif key == "TimingPoints":
			qua.timing_points = _read_timing_points(stream, val_event)
		elif key == "SliderVelocities":
			qua.sv_points = _read_sv_points(stream, val_event)
		elif key == "HitObjects":
			qua.hit_objects = _read_hit_objects(stream, val_event)
		else:
			_skip_value(stream, val_event)

	# DocumentEndEvent -> StreamEndEvent
	stream.next()
	trailing = stream.next()
	if not isinstance(trailing, yaml.StreamEndEvent):
		raise UrcError("syntax", _event_line(trailing), "invalid YAML: expected a single document")

	return qua


def _yaml_line(error: yaml.YAMLError) -> int:
	mark = getattr(error, "problem_mark", None)
	return mark.line + 1 if mark else 1


def _event_line(event: yaml.Event) -> int:
	return event.start_mark.line + 1 if event.start_mark else 1


def _is_null(event: yaml.ScalarEvent) -> bool:
	return event.tag == _NULL_TAG or (
		event.tag is None and event.implicit[0] and event.value in _NULL_VALUES
	)


def _skip_value(stream: _EventStream, first: yaml.Event) -> None:
	if isinstance(first, yaml.ScalarEvent):
		return
	if isinstance(first, yaml.AliasEvent):
		raise UrcError("syntax", _event_line(first), "YAML aliases are not supported")
	if isinstance(first, (yaml.MappingStartEvent, yaml.SequenceStartEvent)):
		depth = 1
		while depth > 0:
			event = stream.next()
			if isinstance(event, (yaml.MappingStartEvent, yaml.SequenceStartEvent)):
				depth += 1
			elif isinstance(event, (yaml.MappingEndEvent, yaml.SequenceEndEvent)):
				depth -= 1
			elif isinstance(event, yaml.AliasEvent):
				raise UrcError("syntax", _event_line(event), "YAML aliases are not supported")


def _read_timing_points(stream: _EventStream, first: yaml.Event) -> list[QuaTimingPoint]:
	if isinstance(first, yaml.ScalarEvent) and _is_null(first):
		return []
	if not isinstance(first, yaml.SequenceStartEvent):
		raise UrcError("syntax", _event_line(first), "TimingPoints must be a list of mappings")

	points: list[QuaTimingPoint] = []
	while True:
		item_event = stream.next()
		if isinstance(item_event, yaml.SequenceEndEvent):
			break
		if not isinstance(item_event, yaml.MappingStartEvent):
			raise UrcError(
				"syntax", _event_line(item_event), "TimingPoints must be a list of mappings"
			)
		start_time = 0
		bpm = 0.0
		signature = 4
		while True:
			key_event = stream.next()
			if isinstance(key_event, yaml.MappingEndEvent):
				break
			if not isinstance(key_event, yaml.ScalarEvent):
				_skip_value(stream, key_event)
				_skip_value(stream, stream.next())
				continue
			key = key_event.value
			val_event = stream.next()
			if key == "StartTime":
				start_time = round_ms(_parse_float(val_event, key, 0.0))
			elif key == "Bpm":
				bpm = _parse_float(val_event, key, 0.0)
			elif key == "Signature":
				signature = _parse_signature(val_event)
			else:
				_skip_value(stream, val_event)
		points.append(QuaTimingPoint(start_time=start_time, bpm=bpm, signature=signature))
	return points


def _read_sv_points(stream: _EventStream, first: yaml.Event) -> list[QuaSvPoint]:
	if isinstance(first, yaml.ScalarEvent) and _is_null(first):
		return []
	if not isinstance(first, yaml.SequenceStartEvent):
		raise UrcError("syntax", _event_line(first), "SliderVelocities must be a list of mappings")

	points: list[QuaSvPoint] = []
	while True:
		item_event = stream.next()
		if isinstance(item_event, yaml.SequenceEndEvent):
			break
		if not isinstance(item_event, yaml.MappingStartEvent):
			raise UrcError(
				"syntax", _event_line(item_event), "SliderVelocities must be a list of mappings"
			)
		start_time = 0
		multiplier = 0.0
		while True:
			key_event = stream.next()
			if isinstance(key_event, yaml.MappingEndEvent):
				break
			if not isinstance(key_event, yaml.ScalarEvent):
				_skip_value(stream, key_event)
				_skip_value(stream, stream.next())
				continue
			key = key_event.value
			val_event = stream.next()
			if key == "StartTime":
				start_time = round_ms(_parse_float(val_event, key, 0.0))
			elif key == "Multiplier":
				multiplier = _parse_float(val_event, key, 0.0)
			else:
				_skip_value(stream, val_event)
		points.append(QuaSvPoint(start_time=start_time, multiplier=multiplier))
	return points


def _read_hit_objects(stream: _EventStream, first: yaml.Event) -> list[QuaHitObject]:
	if isinstance(first, yaml.ScalarEvent) and _is_null(first):
		return []
	if not isinstance(first, yaml.SequenceStartEvent):
		raise UrcError("syntax", _event_line(first), "HitObjects must be a list of mappings")

	objects: list[QuaHitObject] = []
	while True:
		item_event = stream.next()
		if isinstance(item_event, yaml.SequenceEndEvent):
			break
		if not isinstance(item_event, yaml.MappingStartEvent):
			raise UrcError(
				"syntax", _event_line(item_event), "HitObjects must be a list of mappings"
			)
		start_time = 0
		lane = 0
		end_time = 0
		mine = False
		while True:
			key_event = stream.next()
			if isinstance(key_event, yaml.MappingEndEvent):
				break
			if not isinstance(key_event, yaml.ScalarEvent):
				_skip_value(stream, key_event)
				_skip_value(stream, stream.next())
				continue
			key = key_event.value
			val_event = stream.next()
			if key == "StartTime":
				start_time = round_ms(_parse_float(val_event, key, 0.0))
			elif key == "Lane":
				lane = _parse_int(val_event, key, 0)
			elif key == "EndTime":
				end_time = round_ms(_parse_float(val_event, key, 0.0))
			elif key == "Type":
				mine = _parse_hit_object_type(val_event) == 1
			else:
				_skip_value(stream, val_event)
		objects.append(
			QuaHitObject(start_time=start_time, lane=lane, end_time=end_time, mine=mine)
		)
	return objects


def _expect_scalar(event: yaml.Event, key: str) -> yaml.ScalarEvent | None:
	if isinstance(event, yaml.ScalarEvent):
		return None if _is_null(event) else event
	if isinstance(event, yaml.AliasEvent):
		raise UrcError("syntax", _event_line(event), "YAML aliases are not supported")
	raise UrcError("syntax", _event_line(event), f"invalid {key}")


def _parse_str(event: yaml.Event, key: str) -> str | None:
	scalar = _expect_scalar(event, key)
	return scalar.value if scalar is not None else None


def _parse_bool(event: yaml.Event, key: str, default: bool) -> bool:
	scalar = _expect_scalar(event, key)
	if scalar is None:
		return default
	lowered = scalar.value.strip().lower()
	if lowered == "true":
		return True
	if lowered == "false":
		return False
	return default


def _parse_float(event: yaml.Event, key: str, default: float) -> float:
	scalar = _expect_scalar(event, key)
	if scalar is None:
		return default
	try:
		value = float(scalar.value.strip())
	except ValueError:
		raise UrcError("syntax", _event_line(scalar), f"invalid {key}: {scalar.value!r}") from None
	if not math.isfinite(value):
		raise UrcError("syntax", _event_line(scalar), f"invalid {key}: {scalar.value!r}")
	return value


def _parse_int(event: yaml.Event, key: str, default: int) -> int:
	scalar = _expect_scalar(event, key)
	if scalar is None:
		return default
	try:
		value = float(scalar.value.strip())
	except ValueError:
		raise UrcError("syntax", _event_line(scalar), f"invalid {key}: {scalar.value!r}") from None
	if not math.isfinite(value):
		raise UrcError("syntax", _event_line(scalar), f"invalid {key}: {scalar.value!r}")
	return round_ms(value)


def _parse_mode(event: yaml.Event) -> int:
	scalar = _expect_scalar(event, "Mode")
	if scalar is None:
		return 1
	raw = scalar.value.strip()
	mapped = _MODE_NAMES.get(raw.lower())
	if mapped is not None:
		return mapped
	try:
		return int(raw)
	except ValueError:
		raise UrcError(
			"unsupported-version", _event_line(scalar), f"unsupported Quaver mode: {raw}"
		) from None


def _parse_signature(event: yaml.Event) -> int:
	scalar = _expect_scalar(event, "Signature")
	if scalar is None:
		return 4
	raw = scalar.value.strip()
	mapped = _SIGNATURE_NAMES.get(raw.lower())
	if mapped is not None:
		return mapped
	try:
		value = int(raw)
	except ValueError:
		raise UrcError(
			"syntax", _event_line(scalar), f"unsupported time signature: {raw}"
		) from None
	if value == 0:
		return 4
	if value < 1:
		raise UrcError("syntax", _event_line(scalar), f"unsupported time signature: {value}")
	return value


def _parse_hit_object_type(event: yaml.Event) -> int:
	scalar = _expect_scalar(event, "Type")
	if scalar is None:
		return 0
	raw = scalar.value.strip()
	mapped = _HIT_OBJECT_TYPES.get(raw.lower())
	if mapped is not None:
		return mapped
	try:
		value = int(raw)
	except ValueError:
		raise UrcError(
			"syntax", _event_line(scalar), f"unsupported hit object type: {raw}"
		) from None
	if value not in (0, 1):
		raise UrcError("syntax", _event_line(scalar), f"unsupported hit object type: {value}")
	return value
