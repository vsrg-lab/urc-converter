"""Scan orchestration and the public parse entry point."""

import re

from ..error import UrcError
from ..model import Chart, Version
from ..strings import SECTION_INDEX, SECTION_URC, SYNTAX, UNSUPPORTED_VERSION, rule
from .assembly import build
from .checks import finalize_section
from .fields import field_handler
from .state import ParseState

_HEADER_RE = re.compile(r"^@URC (\d+)\.(\d+)$")


def parse(text: str) -> Chart:
    """Parse and validate URC text into a Chart."""
    if text.startswith("\ufeff"):
        text = text[1:]

    state = ParseState()
    lines = text.splitlines()
    _scan(lines, state)

    return build(state, len(lines) + 1)


def _scan(lines: list[str], state: ParseState) -> None:
    current = SECTION_URC
    for offset, raw in enumerate(lines):
        line_no = offset + 1
        text = raw.strip()

        if offset == 0:
            _header(text, line_no, state)
        elif text == "" or text.startswith("#"):
            continue
        elif text.startswith("@"):
            finalize_section(state, current, line_no)
            current = _section(text, line_no, state)
        else:
            handler = field_handler(current)
            if handler is None:
                raise UrcError(SYNTAX, line_no, "unexpected content after @URC header")
            handler(state, text, line_no)

    finalize_section(state, current, len(lines) + 1)


def _header(text: str, line_no: int, state: ParseState) -> None:
    if not text.startswith("@URC"):
        raise UrcError(rule(1), line_no, "first line must be '@URC <version>'")

    match = _HEADER_RE.match(text)
    if match is None:
        raise UrcError(SYNTAX, line_no, f"malformed @URC header: {text!r}")

    major = int(match.group(1))
    minor = int(match.group(2))
    if major != 1 or minor > 1:
        raise UrcError(UNSUPPORTED_VERSION, line_no, f"unsupported version: {major}.{minor}")

    state.version = Version(major, minor)


def _section(text: str, line_no: int, state: ParseState) -> str:
    index = SECTION_INDEX.get(text)
    if index is None:
        raise UrcError(SYNTAX, line_no, f"unknown section: {text}")
    if text in state.seen:
        raise UrcError(rule(3), line_no, f"duplicate section: {text}")
    if index <= state.last_index:
        raise UrcError(rule(3), line_no, f"section out of order: {text}")

    state.seen.add(text)
    state.last_index = index

    return text
