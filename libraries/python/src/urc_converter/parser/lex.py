"""Scalar and list token parsing for URC field values."""

import re

from ..error import UrcError
from ..model import Meter
from ..strings import SYNTAX, rule

_INT_RE = re.compile(r"^-?\d+$")
_FLOAT_RE = re.compile(r"^-?\d+(\.\d+)?$")
_METER_RE = re.compile(r"^(\d+)/(\d+)$")
_TYPE_RE = re.compile(r"^(\d+)(?:\+(\d+))?$")


def int_value(token: str, line: int) -> int:
	if _INT_RE.match(token) is None:
		raise UrcError(SYNTAX, line, f"invalid integer: {token!r}")

	return int(token)


def float_value(token: str, line: int) -> float:
	if _FLOAT_RE.match(token) is None:
		raise UrcError(SYNTAX, line, f"invalid float: {token!r}")

	return float(token)


def float_list(value: str, line: int) -> list[float]:
	values: list[float] = []

	for token in value.split(","):
		token = token.strip()
		if token == "":
			raise UrcError(SYNTAX, line, "empty value in list")

		values.append(float_value(token, line))

	return values


def layout_type(value: str, line: int) -> tuple[int, int]:
	match = _TYPE_RE.match(value)
	if match is None:
		raise UrcError(SYNTAX, line, f"invalid Type value: {value!r}")

	keys = int(match.group(1))
	special = int(match.group(2) or "0")
	if keys < 1 or (match.group(2) is not None and special < 1):
		raise UrcError(SYNTAX, line, "Type values must be positive")

	return keys, special


def meter(token: str, line: int) -> Meter:
	match = _METER_RE.match(token)
	if match is None:
		raise UrcError(rule(17), line, f"invalid meter: {token!r}")

	beats = int(match.group(1))
	note_value = int(match.group(2))
	if beats < 1 or note_value < 1:
		raise UrcError(rule(17), line, f"invalid meter: {token!r}")

	return Meter(beats, note_value)
