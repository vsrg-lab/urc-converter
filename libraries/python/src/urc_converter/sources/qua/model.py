"""Source model of a .qua chart."""

from dataclasses import dataclass, field


@dataclass
class QuaTimingPoint:
	"""One TimingPoints entry."""

	start_time: int
	bpm: float
	signature: int


@dataclass
class QuaSvPoint:
	"""One SliderVelocities entry."""

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
	bpm_does_not_affect_sv: bool = False
	initial_scroll_velocity: float = 0.0
	title: str | None = None
	artist: str | None = None
	creator: str | None = None
	difficulty_name: str | None = None
	timing_points: list[QuaTimingPoint] = field(default_factory=list)
	sv_points: list[QuaSvPoint] = field(default_factory=list)
	hit_objects: list[QuaHitObject] = field(default_factory=list)
