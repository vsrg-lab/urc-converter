"""Finalize-time semantic checks (Phase A completion and Phase B)."""

from ..error import UrcError
from ..strings import (
    METADATA_FIELDS,
    SECTION_JUDGMENT,
    SECTION_LAYOUT,
    SECTION_METADATA,
    SECTION_TIMING,
    rule,
)
from .state import ParseState


def finalize_section(state: ParseState, section: str, line: int) -> None:
    if section == SECTION_METADATA:
        _check_metadata_complete(state, line)
    elif section == SECTION_JUDGMENT:
        _check_judgment(state, line)
    elif section == SECTION_LAYOUT:
        _check_layout(state, line)
    elif section == SECTION_TIMING and not state.timing:
        raise UrcError(rule(14), line, "first timing point must be at timestamp 0")


def _check_metadata_complete(state: ParseState, line: int) -> None:
    for name in METADATA_FIELDS:
        if name not in state.metadata:
            raise UrcError(rule(4), line, f"Metadata is missing field: {name}")


def _check_judgment(state: ParseState, line: int) -> None:
    windows = state.windows
    rates = state.rates

    if windows is None or rates is None:
        raise UrcError(rule(4), line, "Judgment requires both Window and Rate")

    if len(windows) != len(rates):
        raise UrcError(rule(7), line, "Window and Rate must have the same count")

    for earlier, later in zip(windows, windows[1:], strict=False):
        if later < earlier:
            raise UrcError(rule(8), line, "Window values must be ascending")

    for earlier, later in zip(rates, rates[1:], strict=False):
        if later > earlier:
            raise UrcError(rule(9), line, "Rate values must be descending")

    for rate in rates:
        if rate < 0 or rate > 100:
            raise UrcError(rule(10), line, "Rate values must be in 0-100")


def _check_layout(state: ParseState, line: int) -> None:
    layout_type = state.layout_type
    if layout_type is None:
        raise UrcError(rule(4), line, "Layout is missing field: Type")
    if not state.special_seen:
        raise UrcError(rule(4), line, "Layout is missing field: Special")

    special = state.special
    if special is None:
        return

    total = layout_type[0] + layout_type[1]
    for lane in special:
        if lane < 0 or lane >= total:
            raise UrcError(rule(12), line, f"special lane out of range: {lane}")

    if len(set(special)) != len(special):
        raise UrcError(rule(13), line, "duplicate special lanes")
