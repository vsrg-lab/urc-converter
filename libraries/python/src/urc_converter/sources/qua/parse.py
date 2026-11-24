"""Parser for .qua YAML text."""

import math

import yaml

from ...error import UrcError
from .._shared import round_ms
from .model import QuaHitObject, QuaMap, QuaSvPoint, QuaTimingPoint

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


def parse_qua(text: str) -> QuaMap:
	"""Parse .qua YAML text into a source model."""
	cleaned = text.lstrip("\ufeff")
	try:
		root = yaml.compose(cleaned, Loader=yaml.SafeLoader)
	except yaml.YAMLError as error:
		raise UrcError("syntax", _yaml_line(error), f"invalid YAML: {error}") from None

	if not isinstance(root, yaml.MappingNode):
		raise UrcError("syntax", 1, ".qua must be a YAML mapping")

	fields = _mapping(root)
	qua = QuaMap(
		mode=_mode(fields),
		has_scratch_key=_bool(fields, "HasScratchKey", False),
		bpm_does_not_affect_sv=_bool(fields, "BPMDoesNotAffectScrollVelocity", False),
		initial_scroll_velocity=_float(fields, "InitialScrollVelocity", 0.0),
		title=_str(fields, "Title"),
		artist=_str(fields, "Artist"),
		creator=_str(fields, "Creator"),
		difficulty_name=_str(fields, "DifficultyName"),
	)

	for entry in _entries(fields, "TimingPoints"):
		qua.timing_points.append(
			QuaTimingPoint(
				start_time=round_ms(_float(entry, "StartTime", 0.0)),
				bpm=_float(entry, "Bpm", 0.0),
				signature=_signature(entry),
			)
		)

	for entry in _entries(fields, "SliderVelocities"):
		qua.sv_points.append(
			QuaSvPoint(
				start_time=round_ms(_float(entry, "StartTime", 0.0)),
				multiplier=_float(entry, "Multiplier", 0.0),
			)
		)

	for entry in _entries(fields, "HitObjects"):
		qua.hit_objects.append(
			QuaHitObject(
				start_time=round_ms(_float(entry, "StartTime", 0.0)),
				lane=_int(entry, "Lane", 0),
				end_time=round_ms(_float(entry, "EndTime", 0.0)),
				mine=_hit_object_type(entry) == 1,
			)
		)

	return qua


def _yaml_line(error: yaml.YAMLError) -> int:
	mark = getattr(error, "problem_mark", None)
	return mark.line + 1 if mark else 1


def _node_line(node: yaml.Node) -> int:
	return node.start_mark.line + 1 if node.start_mark else 1


def _is_null(node: yaml.Node) -> bool:
	return isinstance(node, yaml.ScalarNode) and node.tag == _NULL_TAG


def _mapping(node: yaml.MappingNode) -> dict[str, yaml.Node]:
	result: dict[str, yaml.Node] = {}
	for key_node, value_node in node.value:
		if isinstance(key_node, yaml.ScalarNode):
			result[key_node.value] = value_node
	return result


def _entries(fields: dict[str, yaml.Node], key: str) -> list[dict[str, yaml.Node]]:
	node = fields.get(key)
	if node is None or _is_null(node):
		return []
	if not isinstance(node, yaml.SequenceNode):
		raise UrcError("syntax", _node_line(node), f"{key} must be a list of mappings")
	items: list[dict[str, yaml.Node]] = []
	for item in node.value:
		if not isinstance(item, yaml.MappingNode):
			raise UrcError("syntax", _node_line(item), f"{key} must be a list of mappings")
		items.append(_mapping(item))
	return items


def _scalar(fields: dict[str, yaml.Node], key: str) -> yaml.ScalarNode | None:
	node = fields.get(key)
	if node is None or _is_null(node):
		return None
	if not isinstance(node, yaml.ScalarNode):
		raise UrcError("syntax", _node_line(node), f"invalid {key}")
	return node


def _str(fields: dict[str, yaml.Node], key: str) -> str | None:
	node = _scalar(fields, key)
	return node.value if node is not None else None


def _bool(fields: dict[str, yaml.Node], key: str, default: bool) -> bool:
	node = _scalar(fields, key)
	if node is None:
		return default
	lowered = node.value.strip().lower()
	if lowered == "true":
		return True
	if lowered == "false":
		return False
	return default


def _float(fields: dict[str, yaml.Node], key: str, default: float) -> float:
	node = _scalar(fields, key)
	if node is None:
		return default
	try:
		value = float(node.value.strip())
	except ValueError:
		raise UrcError("syntax", _node_line(node), f"invalid {key}: {node.value!r}") from None
	if not math.isfinite(value):
		raise UrcError("syntax", _node_line(node), f"invalid {key}: {node.value!r}")
	return value


def _int(fields: dict[str, yaml.Node], key: str, default: int) -> int:
	node = _scalar(fields, key)
	if node is None:
		return default
	try:
		value = float(node.value.strip())
	except ValueError:
		raise UrcError("syntax", _node_line(node), f"invalid {key}: {node.value!r}") from None
	if not math.isfinite(value):
		raise UrcError("syntax", _node_line(node), f"invalid {key}: {node.value!r}")
	return round_ms(value)


def _mode(fields: dict[str, yaml.Node]) -> int:
	node = _scalar(fields, "Mode")
	if node is None:
		return 1
	raw = node.value.strip()
	mapped = _MODE_NAMES.get(raw.lower())
	if mapped is not None:
		return mapped
	try:
		return int(raw)
	except ValueError:
		raise UrcError(
			"unsupported-version", _node_line(node), f"unsupported Quaver mode: {raw}"
		) from None


def _signature(fields: dict[str, yaml.Node]) -> int:
	node = _scalar(fields, "Signature")
	if node is None:
		return 4
	raw = node.value.strip()
	mapped = _SIGNATURE_NAMES.get(raw.lower())
	if mapped is not None:
		return mapped
	try:
		value = int(raw)
	except ValueError:
		raise UrcError(
			"syntax", _node_line(node), f"unsupported time signature: {raw}"
		) from None
	if value == 0:
		return 4
	if value < 1:
		raise UrcError("syntax", _node_line(node), f"unsupported time signature: {value}")
	return value


def _hit_object_type(fields: dict[str, yaml.Node]) -> int:
	node = _scalar(fields, "Type")
	if node is None:
		return 0
	raw = node.value.strip()
	mapped = _HIT_OBJECT_TYPES.get(raw.lower())
	if mapped is not None:
		return mapped
	try:
		value = int(raw)
	except ValueError:
		raise UrcError(
			"syntax", _node_line(node), f"unsupported hit object type: {raw}"
		) from None
	if value not in (0, 1):
		raise UrcError("syntax", _node_line(node), f"unsupported hit object type: {value}")
	return value
