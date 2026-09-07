"""Source model of an O2Jam (.ojn) chart file."""

from dataclasses import dataclass


@dataclass
class OjnEvent:
	"""One event of a note-section package, in file order."""

	measure: int
	# Package grid position: raw event index over the package total (f64).
	position: float
	channel: int
	# f32 payload of channels 0 (measure fraction) and 1 (BPM change).
	value: float
	# Note kind from `type % 4`: 0 normal, 2 hold, 3 release.
	type: int
	# Byte offset of the event, for error locations.
	offset: int


@dataclass
class OjnDifficulty:
	"""Events of one difficulty section."""

	index: int
	events: list[OjnEvent]


@dataclass
class OjnFile:
	"""Parsed OJN file; the header BPM drives the timing walk."""

	title: str
	artist: str
	noter: str
	bpm: float
	difficulties: list[OjnDifficulty]
