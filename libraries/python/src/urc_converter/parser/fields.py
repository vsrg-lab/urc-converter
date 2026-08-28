"""Per-section field line parsing (Phase A content handlers)."""

from collections.abc import Callable

from ..error import UrcError
from ..model import TimingPoint
from ..strings import (
    JUDGMENT_FIELD_RATE,
    JUDGMENT_FIELD_WINDOW,
    LAYOUT_FIELD_SPECIAL,
    LAYOUT_FIELD_TYPE,
    METADATA_FIELDS,
    SECTION_JUDGMENT,
    SECTION_LAYOUT,
    SECTION_METADATA,
    SECTION_NOTES,
    SECTION_TIMING,
    SPECIAL_NONE,
    SYNTAX,
    rule,
)
from . import lex
from .state import ParseState, RawNote


def metadata_field(state: ParseState, text: str, line: int) -> None:
    name, separator, value = text.partition(":")

    if not separator:
        raise UrcError(SYNTAX, line, f"expected 'Field: Value', got: {text!r}")

    name = name.strip()
    if name not in METADATA_FIELDS:
        raise UrcError(rule(6), line, f"unknown metadata field: {name}")
    if name in state.metadata:
        raise UrcError(SYNTAX, line, f"duplicate metadata field: {name}")

    value = value.strip()
    if value == "":
        raise UrcError(rule(5), line, f"metadata field has no value: {name}")

    state.metadata[name] = value


def judgment_field(state: ParseState, text: str, line: int) -> None:
    name, separator, value = text.partition(":")

    if not separator:
        raise UrcError(SYNTAX, line, f"expected 'Field: values', got: {text!r}")

    name = name.strip()
    if name == JUDGMENT_FIELD_WINDOW:
        if state.windows is not None:
            raise UrcError(SYNTAX, line, "duplicate judgment field: Window")
        state.windows = lex.float_list(value, line)
    elif name == JUDGMENT_FIELD_RATE:
        if state.rates is not None:
            raise UrcError(SYNTAX, line, "duplicate judgment field: Rate")
        state.rates = lex.float_list(value, line)
    else:
        raise UrcError(rule(6), line, f"unknown judgment field: {name}")


def layout_field(state: ParseState, text: str, line: int) -> None:
    name, separator, value = text.partition(":")

    if not separator:
        raise UrcError(SYNTAX, line, f"expected 'Field: Value', got: {text!r}")

    name = name.strip()
    if name == LAYOUT_FIELD_TYPE:
        if state.layout_type is not None:
            raise UrcError(SYNTAX, line, "duplicate layout field: Type")
        state.layout_type = lex.layout_type(value.strip(), line)
    elif name == LAYOUT_FIELD_SPECIAL:
        if state.special_seen:
            raise UrcError(SYNTAX, line, "duplicate layout field: Special")

        if value.strip() == SPECIAL_NONE:
            state.special = None
        else:
            lanes: list[int] = []
            for token in value.split(","):
                token = token.strip()
                if token == "":
                    raise UrcError(SYNTAX, line, "empty lane in Special list")
                lanes.append(lex.int_value(token, line))

            state.special = lanes

        state.special_seen = True
    else:
        raise UrcError(rule(6), line, f"unknown layout field: {name}")


def timing_point(state: ParseState, text: str, line: int) -> None:
    fields = [field.strip() for field in text.split(",")]

    if len(fields) not in (3, 4):
        raise UrcError(SYNTAX, line, f"timing point needs 3 or 4 fields, got {len(fields)}")

    timestamp = lex.int_value(fields[0], line)
    bpm = lex.float_value(fields[1], line)
    meter = lex.meter(fields[2], line)
    multiplier = None

    if len(fields) == 4 and fields[3] != "":
        multiplier = lex.float_value(fields[3], line)

    if not state.timing:
        if timestamp != 0:
            raise UrcError(rule(14), line, "first timing point must be at timestamp 0")
    elif timestamp <= state.timing[-1].timestamp_ms:
        raise UrcError(rule(15), line, "timing timestamps must be strictly ascending")

    if bpm <= 0:
        raise UrcError(rule(16), line, "bpm must be positive")

    state.timing.append(TimingPoint(timestamp, bpm, meter, multiplier))


def note_line(state: ParseState, text: str, line: int) -> None:
    fields = [field.strip() for field in text.split(",")]

    if len(fields) != 3:
        raise UrcError(SYNTAX, line, f"note needs 3 fields, got {len(fields)}")

    state.notes.append(
        RawNote(lex.int_value(fields[0], line), lex.int_value(fields[1], line), fields[2], line)
    )


_HANDLERS = {
    SECTION_METADATA: metadata_field,
    SECTION_JUDGMENT: judgment_field,
    SECTION_LAYOUT: layout_field,
    SECTION_TIMING: timing_point,
    SECTION_NOTES: note_line,
}


def field_handler(section: str) -> Callable[[ParseState, str, int], None] | None:
    """Content handler for a section, or None outside data sections."""
    return _HANDLERS.get(section)
