"""Source model of an osu!mania beatmap."""

from dataclasses import dataclass, field


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
