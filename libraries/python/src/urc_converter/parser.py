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
            self._check_layout(line)
        elif section == "@Timing":
            if not self._timing:
                raise UrcError("rule:14", line, "first timing point must be at timestamp 0")

    def _check_judgment(self, line: int) -> None:
        assert self._windows is not None and self._rates is not None

        if len(self._windows) != len(self._rates):
            raise UrcError("rule:7", line, "Window and Rate must have the same count")

        for earlier, later in zip(self._windows, self._windows[1:]):
            if later < earlier:
                raise UrcError("rule:8", line, "Window values must be ascending")

        for earlier, later in zip(self._rates, self._rates[1:]):
            if later > earlier:
                raise UrcError("rule:9", line, "Rate values must be descending")

        for rate in self._rates:
            if rate < 0 or rate > 100:
                raise UrcError("rule:10", line, "Rate values must be in 0-100")

    def _check_layout(self, line: int) -> None:
        assert self._type is not None

        keys, special = self._type
        total = keys + special

        if self._special is not None:
            for lane in self._special:
                if lane < 0 or lane >= total:
                    raise UrcError("rule:12", line, f"special lane out of range: {lane}")

            if len(set(self._special)) != len(self._special):
                raise UrcError("rule:13", line, "duplicate special lanes")

    def _content(self, section: str, text: str, line: int) -> None:
        if section == "@Metadata":
            self._metadata_field(text, line)
        elif section == "@Judgment":
            self._judgment_field(text, line)
        elif section == "@Layout":
            self._layout_field(text, line)
        elif section == "@Timing":
            self._timing_point(text, line)
        elif section == "@Notes":
            self._note(text, line)
        else:
            raise UrcError("syntax", line, "unexpected content after @URC header")

    def _metadata_field(self, text: str, line: int) -> None:
        name, separator, value = text.partition(":")

        if not separator:
            raise UrcError("syntax", line, f"expected 'Field: Value', got: {text!r}")

        name = name.strip()
        if name not in _METADATA_FIELDS:
            raise UrcError("rule:6", line, f"unknown metadata field: {name}")
        if name in self._metadata:
            raise UrcError("syntax", line, f"duplicate metadata field: {name}")

        value = value.strip()
        if value == "":
            raise UrcError("rule:5", line, f"metadata field has no value: {name}")

        self._metadata[name] = value

    def _judgment_field(self, text: str, line: int) -> None:
        name, separator, value = text.partition(":")

        if not separator:
            raise UrcError("syntax", line, f"expected 'Field: Value', got: {text!r}")

        name = name.strip()
        if name == "Window":
            if self._windows is not None:
                raise UrcError("syntax", line, "duplicate judgment field: Window")
            self._windows = _float_list(value, line)
        elif name == "Rate":
            if self._rates is not None:
                raise UrcError("syntax", line, "duplicate judgment field: Rate")
            self._rates = _float_list(value, line)
        else:
            raise UrcError("rule:6", line, f"unknown judgment field: {name}")

    def _layout_field(self, text: str, line: int) -> None:
        name, separator, value = text.partition(":")

        if not separator:
            raise UrcError("syntax", line, f"expected 'Field: Value', got: {text!r}")

        name = name.strip()
        if name == "Type":
            if self._type is not None:
                raise UrcError("syntax", line, "duplicate layout field: Type")
            self._type = self._layout_type(value.strip(), line)
        elif name == "Special":
            if self._special_seen:
                raise UrcError("syntax", line, "duplicate layout field: Special")

            if value.strip() == "None":
                self._special = None
            else:
                lanes = []
                for token in value.split(","):
                    token = token.strip()
                    if token == "":
                        raise UrcError("syntax", line, "empty lane in Special list")
                    lanes.append(_int(token, line))

                self._special = lanes

            self._special_seen = True
        else:
            raise UrcError("rule:6", line, f"unknown layout field: {name}")

    def _timing_point(self, text: str, line: int) -> None:
        fields = [field.strip() for field in text.split(",")]

        if len(fields) not in (3, 4):
            raise UrcError("syntax", line, f"timing point needs 3 or 4 fields, got {len(fields)}")

        timestamp = _int(fields[0], line)
        bpm = _float(fields[1], line)
        meter = self._meter(fields[2], line)
        multiplier = None

        if len(fields) == 4 and fields[3] != "":
            multiplier = _float(fields[3], line)

        if not self._timing:
            if timestamp != 0:
                raise UrcError("rule:14", line, "first timing point must be at timestamp 0")
        elif timestamp <= self._timing[-1][0].timestamp_ms:
            raise UrcError("rule:15", line, "timing timestamps must be strictly ascending")

        if bpm <= 0:
            raise UrcError("rule:16", line, "bpm must be positive")

        self._timing.append((TimingPoint(timestamp, bpm, meter, multiplier), line))

    def _note(self, text: str, line: int) -> None:
        fields = [field.strip() for field in text.split(",")]

        if len(fields) != 3:
            raise UrcError("syntax", line, f"note needs 3 fields, got {len(fields)}")

        self._notes.append((_int(fields[0], line), _int(fields[1], line), fields[2], line))

    def _build(self, end_line: int) -> Chart:
        for name in _REQUIRED_SECTIONS:
            if name not in self._seen:
                raise UrcError("rule:2", end_line, f"missing required section: {name}")

        assert self._type is not None

        layout = Layout(self._type[0], self._type[1], self._special)
        total = layout.total_lanes
        notes = []
        open_ls: dict[int, int] = {}
        ordered = sorted(self._notes, key=lambda entry: (entry[0], entry[1]))

        for timestamp, lane, type_token, line in ordered:
            if timestamp < 0:
                raise UrcError("rule:22", line, "note timestamps must be non-negative")

            if lane < 0 or lane >= total:
                raise UrcError("rule:18", line, f"lane out of range: {lane}")

            note_type = _NOTE_TYPES.get(type_token)
            if note_type is None:
                raise UrcError("rule:19", line, f"unknown note type: {type_token!r}")

            if note_type is NoteType.LE:
                if lane not in open_ls:
                    raise UrcError("rule:20", line, f"LE without an open LS on lane {lane}")
                del open_ls[lane]
            elif note_type is NoteType.LS:
                if lane in open_ls:
                    raise UrcError("rule:21", line, f"overlapping long notes on lane {lane}")
                open_ls[lane] = line

            notes.append(Note(timestamp, lane, note_type))

        if open_ls:
            lane, line = next(iter(open_ls.items()))
            raise UrcError("rule:20", line, f"unterminated LS on lane {lane}")

        judgment = None
        if self._windows is not None:
            assert self._rates is not None
            judgment = Judgment(self._windows, self._rates)

        return Chart(
            format_version=self._version,
            metadata=Metadata(
                original=self._metadata["Original"],
                title=self._metadata["Title"],
                artist=self._metadata["Artist"],
                creator=self._metadata["Creator"],
                version=self._metadata["Version"]
            ),
            judgment=judgment,
            layout=layout,
            timing=[point for point, _ in self._timing],
            notes=notes
        )

    @staticmethod
    def _layout_type(value: str, line: int) -> tuple[int, int]:
        match = _TYPE_RE.match(value)

        if match is None:
            raise UrcError("syntax", line, f"invalid Type value: {value!r}")

        keys = int(match.group(1))
        special = int(match.group(2) or "0")

        if keys < 1 or (match.group(2) is not None and special < 1):
            raise UrcError("syntax", line, "Type values must be positive")

        return keys, special

    @staticmethod
    def _meter(token: str, line: int) -> Meter:
        match = _METER_RE.match(token)
        if match is None:
            raise UrcError("rule:17", line, f"invalid meter: {token!r}")

        beats = int(match.group(1))
        note_value = int(match.group(2))

        if beats < 1 or note_value < 1:
            raise UrcError("rule:17", line, f"invalid meter: {token!r}")

        return Meter(beats, note_value)