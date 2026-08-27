"""Parser for URC 1.x documents."""

import re

from .error import UrcError
from .model import Chart, Judgment, Layout, Meter, Metadata, Note, NoteType, TimingPoint, Version


_HEADER_RE = re.compile(r"^@URC (\d+)\.(\d+)$")
_INT_RE = re.compile(r"^-?\d+$")
_FLOAT_RE = re.compile(r"^-?\d+(\.\d+)?$")
_METER_RE = re.compile(r"^(\d+)/(\d+)$")
_TYPE_RE = re.compile(r"^(\d+)(?:\+(\d+))?$")

_SECTION_INDEX = {
    "@URC": 0,
    "@Metadata": 1,
    "@Judgment": 2,
    "@Layout": 3,
    "@Timing": 4,
    "@Notes": 5
}
_REQUIRED_SECTIONS = ("@Metadata", "@Layout", "@Timing", "@Notes")
_METADATA_FIELDS = ("Original", "Title", "Artist", "Creator", "Version")
_NOTE_TYPES = {
    "N": NoteType.N,
    "LS": NoteType.LS,
    "LE": NoteType.LE,
    "M": NoteType.M,
    "F": NoteType.F
}


def parse(text: str) -> Chart:
    """Parse and validate URC text into a Chart."""
    if text.startswith("\ufeff"):
        text = text[1:]

    return _Parser(text.splitlines()).run()


def _int(token: str, line: int) -> int:
    if _INT_RE.match(token) is None:
        raise UrcError("syntax", line, f"invalid integer: {token!r}")

    return int(token)


def _float(token: str, line: int) -> float:
    if _FLOAT_RE.match(token) is None:
        raise UrcError("syntax", line, f"invalid float: {token!r}")

    return float(token)


def _float_list(value: str, line: int) -> list[float]:
    values = []

    for token in value.split(","):
        token = token.strip()
        if token == "":
            raise UrcError("syntax", line, "empty value in list")

        values.append(_float(token, line))

    return values


class _Parser:
    """Scans lines in document order and accumulates raw section content."""
    def __init__(self, lines: list[str]):
        self._lines = lines
        self._seen: set[str] = {"@URC"}
        self._last_index = 0
        self._version = Version(1, 1)
        self._metadata: dict[str, str] = {}
        self._windows: list[float] | None = None
        self._rates: list[float] | None = None
        self._type: tuple[int, int] | None = None
        self._special: list[int] | None = None
        self._special_seen = False
        self._timing: list[tuple[TimingPoint, int]] = []
        self._notes: list[tuple[int, int, str, int]] = []

    def run(self) -> Chart:
        current = "@URC"
        for offset, raw in enumerate(self._lines):
            line_no = offset + 1
            text = raw.strip()

            if line_no == 1:
                self._header(text, line_no)
                continue

            if text == "" or text.startswith("#"):
                continue

            if text.startswith("@"):
                self._finalize(current, line_no)
                current = self._section(text, line_no)
                continue

            self._content(current, text, line_no)

        end_line = len(self._lines) + 1
        self._finalize(current, end_line)

        return self._build(end_line)

    def _header(self, text: str, line: int) -> None:
        if not text.startswith("@URC"):
            raise UrcError("rule:1", line, "first line must be '@URC <version>'")

        match = _HEADER_RE.match(text)
        if match is None:
            raise UrcError("syntax", line, f"malformed @URC header: {text!r}")

        major = int(match.group(1))
        minor = int(match.group(2))
        if major != 1 or minor > 1:
            raise UrcError("unsupported-version", line, f"unsupported version: {major}.{minor}")

        self._version = Version(major, minor)

    def _section(self, name: str, line: int) -> str:
        index = _SECTION_INDEX.get(name)
        if index is None:
            raise UrcError("syntax", line, f"unknown section: {name}")
        if name in self._seen:
            raise UrcError("rule:3", line, f"duplicate section: {name}")
        if index <= self._last_index:
            raise UrcError("rule:3", line, f"section out of order: {name}")

        self._seen.add(name)
        self._last_index = index

        return name

    def _finalize(self, section: str, line: int) -> None:
        if section == "@Metadata":
            for field in _METADATA_FIELDS:
                if field not in self._metadata:
                    raise UrcError("rule:4", line, f"Metadata is missing field: {field}")
        elif section == "@Judgment":
            if self._windows is None or self._rates is None:
                raise UrcError("rule:4", line, "Judgment requires both Window and Rate")
            self._check_judgment(line)
        elif section == "@Layout":
            if self._type is None:
                raise UrcError("rule:4", line, "Layout is missing field: Type")
            if not self._special_seen:
                raise UrcError("rule:4", line, "Layout is missing field: Special")