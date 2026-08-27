"""URC data model shared by the parser, writer, and converters."""

import enum
from dataclasses import dataclass


class NoteType(enum.Enum):
    """Types of a chart object in the @Notes section."""
    N = "N"
    LS = "LS"
    LE = "LE"
    M = "M"
    F = "F"


@dataclass
class Version:
    """File format version from the @URC header."""
    major: int
    minor: int


@dataclass
class Metadata:
    """Song and chart metadata from the @Metadata secion."""
    original: str
    title: str
    artist: str
    creator: str
    version: str


@dataclass
class Judgment:
    """Timing windows (ms) and scoring rates from the optional @Judgment section."""
    windows: list[float]
    rates: list[float]


@dataclass
class Layout:
    """Key layout form the @Layout section."""
    keys: int
    special_keys: int
    special_lanes: list[int] | None

    @property
    def total_lanes(self) -> int:
        return self.keys + self.special_keys


@dataclass
class Meter:
    """Time signature numerator and denominator."""
    beats: int
    note_value: int


@dataclass
class TimingPoint:
    """One @Timing entry, multiplier is None when omitted."""
    timestamp_ms: int
    bpm: float
    meter: Meter
    multiplier: float | None


@dataclass
class Note:
    """One @Notes entry."""
    timestamp_ms: int
    lane: int
    type: NoteType


@dataclass
class Chart:
    """Complete parsed URC document."""
    format_version: Version
    metadata: Metadata
    judgment: Judgment | None
    layout: Layout
    timing: list[TimingPoint]
    notes: list[Note]
