"""Chart assembly: note validation (Phase C) and model construction."""

from ..error import UrcError
from ..model import Chart, Judgment, Layout, Metadata, Note, NoteType
from ..strings import REQUIRED_SECTIONS, rule
from .state import ParseState

_NOTE_TYPES = {member.value: member for member in NoteType}


def build(state: ParseState, end_line: int) -> Chart:
	for name in REQUIRED_SECTIONS:
		if name not in state.seen:
			raise UrcError(rule(2), end_line, f"missing required section: {name}")

	assert state.layout_type is not None
	keys, special_keys = state.layout_type
	layout = Layout(keys, special_keys, state.special)
	total = layout.total_lanes

	notes: list[Note] = []
	open_ls: dict[int, int] = {}
	ordered = sorted(state.notes, key=lambda raw: (raw.timestamp, raw.lane))

	for raw in ordered:
		if raw.timestamp < 0:
			raise UrcError(rule(22), raw.line, "note timestamps must be non-negative")

		if raw.lane < 0 or raw.lane >= total:
			raise UrcError(rule(18), raw.line, f"lane out of range: {raw.lane}")

		note_type = _NOTE_TYPES.get(raw.type_token)
		if note_type is None:
			raise UrcError(rule(19), raw.line, f"unknown note type: {raw.type_token!r}")

		if note_type is NoteType.LE:
			if raw.lane not in open_ls:
				raise UrcError(rule(20), raw.line, f"LE without an open LS on lane {raw.lane}")
			del open_ls[raw.lane]
		elif note_type is NoteType.LS:
			if raw.lane in open_ls:
				raise UrcError(rule(21), raw.line, f"overlapping long notes on lane {raw.lane}")
			open_ls[raw.lane] = raw.line

		notes.append(Note(raw.timestamp, raw.lane, note_type))

	if open_ls:
		lane, line = next(iter(open_ls.items()))
		raise UrcError(rule(20), line, f"unterminated LS on lane {lane}")

	judgment = None
	if state.windows is not None:
		assert state.rates is not None
		judgment = Judgment(state.windows, state.rates)

	metadata = state.metadata
	return Chart(
		format_version=state.version,
		metadata=Metadata(
			original=metadata["Original"],
			title=metadata["Title"],
			artist=metadata["Artist"],
			creator=metadata["Creator"],
			version=metadata["Version"],
		),
		judgment=judgment,
		layout=layout,
		timing=state.timing,
		notes=notes,
	)
