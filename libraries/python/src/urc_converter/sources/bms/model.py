"""Source model of a BMS-family chart."""

from dataclasses import dataclass, field


@dataclass
class BmsChart:
	"""Source model of a BMS-family chart."""

	pms: bool
	base: int = 36
	title: str | None = None
	artist: str | None = None
	play_level: str | None = None
	bpm: float | None = None
	lntype: int = 1
	lnobj: str | None = None
	bpm_defs: dict[str, float] = field(default_factory=dict)
	stop_defs: dict[str, float] = field(default_factory=dict)
	scroll_defs: dict[str, float] = field(default_factory=dict)
	rates: dict[int, float] = field(default_factory=dict)
	measures: dict[int, dict[str, list[str]]] = field(default_factory=dict)
