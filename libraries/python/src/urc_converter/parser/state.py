"""Mutable scan state shared by the parser modules."""

from dataclasses import dataclass, field

from ..model import TimingPoint, Version
from ..strings import SECTION_URC


@dataclass
class RawNote:
    timestamp: int
    lane: int
    type_token: str
    line: int


@dataclass
class ParseState:
    seen: set[str] = field(default_factory=lambda: {SECTION_URC})
    last_index: int = 0
    version: Version = field(default_factory=lambda: Version(1, 1))
    metadata: dict[str, str] = field(default_factory=dict)
    windows: list[float] | None = None
    rates: list[float] | None = None
    layout_type: tuple[int, int] | None = None
    special: list[int] | None = None
    special_seen: bool = False
    timing: list[TimingPoint] = field(default_factory=list)
    notes: list[RawNote] = field(default_factory=list)
