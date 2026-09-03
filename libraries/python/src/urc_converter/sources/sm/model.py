"""Source model for StepMania (.sm/.ssc) simfiles."""

from dataclasses import dataclass, field


@dataclass
class Timing:
	"""Timing segments in effect at the song level or for one chart."""

	offset: float = 0.0
	bpms: list[tuple[float, float]] = field(default_factory=list)
	stops: list[tuple[float, float]] = field(default_factory=list)
	delays: list[tuple[float, float]] = field(default_factory=list)
	warps: list[tuple[float, float]] = field(default_factory=list)
	scrolls: list[tuple[float, float]] = field(default_factory=list)
	timesigs: list[tuple[float, int, int]] = field(default_factory=list)
	fakes: list[tuple[float, float]] = field(default_factory=list)


@dataclass
class SmNote:
	"""One note head; hold/roll pairs carry the tail row."""

	row: int
	track: int
	kind: str  # tap | hold | roll | mine | lift | fake
	tail_row: int | None = None


@dataclass
class SmChart:
	"""One chart block (#NOTES in .sm, #NOTEDATA in .ssc)."""

	steps_type: str
	description: str = ""
	difficulty: str = ""
	chartname: str = ""
	credit: str = ""
	timing: Timing | None = None  # None: inherit the song-level timing
	notes: list[SmNote] = field(default_factory=list)


@dataclass
class SmFile:
	"""Parsed simfile: song metadata, song timing, and charts."""

	title: str = ""
	subtitle: str = ""
	artist: str = ""
	credit: str = ""
	timing: Timing = field(default_factory=Timing)
	charts: list[SmChart] = field(default_factory=list)
