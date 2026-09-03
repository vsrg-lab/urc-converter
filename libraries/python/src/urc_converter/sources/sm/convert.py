"""Mapper from the StepMania source model onto URC charts."""

from itertools import groupby

from ...error import UrcError
from ...model import Chart, Layout, Metadata, Note, NoteType, Version
from .._shared import build_timing, check_hold_overlap, round_ms
from .model import SmChart, SmFile, SmNote, Timing
from .parse import resolve_lanes

_MEASURE_ROWS = 192
_FAST_BPM_WARP = 9999999.0
_ROLL_TAP_SPACING_MS = 500

_TYPE_ORDER = {NoteType.N: 0, NoteType.LS: 1, NoteType.LE: 2, NoteType.M: 3, NoteType.F: 4}

_DIFFICULTY_NAMES = {
	"beginner": "Beginner",
	"easy": "Easy",
	"basic": "Easy",
	"light": "Easy",
	"medium": "Medium",
	"another": "Medium",
	"trick": "Medium",
	"standard": "Medium",
	"difficult": "Medium",
	"hard": "Hard",
	"ssr": "Hard",
	"maniac": "Hard",
	"heavy": "Hard",
	"smaniac": "Challenge",
	"challenge": "Challenge",
	"expert": "Challenge",
	"oni": "Challenge",
	"edit": "Edit",
}


def convert_sm(simfile: SmFile) -> list[Chart]:
	"""Convert every chart of a simfile into URC charts."""
	return [_convert_chart(simfile, chart) for chart in simfile.charts]


def _convert_chart(simfile: SmFile, chart: SmChart) -> Chart:
	lanes = resolve_lanes(chart.steps_type)
	timing = chart.timing if chart.timing is not None else simfile.timing
	offset, bpm_segs, stop_segs, warp_segs = _preprocess(timing)
	intervals = _warp_intervals(warp_segs + timing.warps)
	notes = _filter_notes(chart.notes, intervals)

	entries: list[tuple[int, int, str, object]] = []
	for start, dest in intervals:
		entries.append((start, 0, "warp", dest))
	for beat, seconds in timing.delays:
		entries.append((_rows(beat), 1, "delay", seconds))
	for index, note in enumerate(notes):
		entries.append((note.row, 2, "note", index))
		if note.tail_row is not None:
			entries.append((note.tail_row, 2, "tail", index))
	for beat, seconds in stop_segs:
		entries.append((_rows(beat), 3, "stop", seconds))
	for beat, bpm in bpm_segs:
		entries.append((_rows(beat), 4, "bpm", bpm))
	for beat, numerator, denominator in timing.timesigs:
		entries.append((_rows(beat), 5, "timesig", (numerator, denominator)))
	for beat, ratio in timing.scrolls:
		entries.append((_rows(beat), 6, "scroll", ratio))
	max_row = max((entry[0] for entry in entries), default=0)
	entries.extend(
		(row, 2, "anchor", None) for row in range(0, max_row + _MEASURE_ROWS, _MEASURE_ROWS)
	)

	head_times: list[float] = [0.0] * len(notes)
	tail_times: dict[int, float] = {}
	bpm_points: list[tuple[int, float, int, int]] = []
	sv_points: list[tuple[int, float]] = []
	anchors: list[int] = []

	seconds = -offset
	bpm: float | None = None
	meter = (4, 4)
	multiplier = 1.0
	warping = False
	warp_dest = 0
	prev_row: int | None = None

	entries.sort(key=lambda entry: (entry[0], entry[1]))
	for row, group in groupby(entries, key=lambda entry: entry[0]):
		if prev_row is not None and not warping and bpm is not None:
			seconds += (row - prev_row) / 48.0 * 60.0 / bpm
		prev_row = row
		if warping and row >= warp_dest:
			warping = False

		new_bpm, new_meter, new_multiplier = bpm, meter, multiplier
		for _, _priority, kind, value in group:
			if kind == "warp":
				if warping:
					warp_dest = max(warp_dest, value)
				else:
					warping = True
					warp_dest = value
			elif kind == "delay":
				seconds += value
			elif kind == "note":
				head_times[value] = seconds
			elif kind == "tail":
				tail_times[value] = seconds
			elif kind == "anchor":
				anchors.append(round_ms(seconds * 1000.0))
			elif kind == "stop":
				seconds += value
			elif kind == "bpm":
				new_bpm = value
			elif kind == "timesig":
				new_meter = value
			else:
				new_multiplier = value

		if new_bpm is not None and (new_bpm, new_meter) != (bpm, meter):
			bpm_points.append((round_ms(seconds * 1000.0), new_bpm, *new_meter))
		if new_multiplier != multiplier:
			sv_points.append((round_ms(seconds * 1000.0), new_multiplier))
		bpm, meter, multiplier = new_bpm, new_meter, new_multiplier

	urc_notes = _build_urc_notes(timing, notes, head_times, tail_times)
	first_note_time = min(
		(time for time, _lane, note_type in urc_notes if note_type is not NoteType.LE),
		default=0,
	)

	timing_points = build_timing(
		bpm_points=bpm_points,
		sv_points=sv_points,
		first_note_time=first_note_time,
		source=".sm",
		measure_anchor_ms=next((time for time in anchors if time >= first_note_time), None),
	)

	urc_notes.sort(key=lambda item: (item[0], item[1], _TYPE_ORDER[item[2]]))
	final_notes = [
		Note(timestamp_ms=time - first_note_time, lane=lane, type=note_type)
		for time, lane, note_type in urc_notes
	]
	check_hold_overlap(final_notes)

	title = " ".join(part for part in (simfile.title, simfile.subtitle) if part) or "Unknown"
	return Chart(
		format_version=Version(major=1, minor=1),
		metadata=Metadata(
			original="StepMania",
			title=title,
			artist=simfile.artist or "Unknown",
			creator=chart.credit or simfile.credit or "Unknown",
			version=chart.chartname or _difficulty_name(chart.difficulty, chart.description),
		),
		judgment=None,
		layout=Layout(keys=lanes, special_keys=0, special_lanes=None),
		timing=timing_points,
		notes=final_notes,
	)


def _build_urc_notes(
	timing: Timing,
	notes: list[SmNote],
	head_times: list[float],
	tail_times: dict[int, float],
) -> list[tuple[int, int, NoteType]]:
	fake_ranges = [(_rows(beat), _rows(beat) + _rows(length)) for beat, length in timing.fakes]
	urc_notes: list[tuple[int, int, NoteType]] = []

	for index, note in enumerate(notes):
		head_ms = round_ms(head_times[index] * 1000.0)
		if any(start <= note.row < end for start, end in fake_ranges):
			urc_notes.append((head_ms, note.track, NoteType.F))
		elif note.kind == "hold":
			tail_ms = round_ms(tail_times[index] * 1000.0)
			if tail_ms <= head_ms:
				raise UrcError("syntax", 1, f"hold on lane {note.track} collapses to zero length")
			urc_notes.append((head_ms, note.track, NoteType.LS))
			urc_notes.append((tail_ms, note.track, NoteType.LE))
		elif note.kind == "roll":
			end_ms = round_ms(tail_times[index] * 1000.0)
			urc_notes.append((head_ms, note.track, NoteType.N))
			tap_ms = head_ms + _ROLL_TAP_SPACING_MS
			while tap_ms < end_ms:
				urc_notes.append((tap_ms, note.track, NoteType.N))
				tap_ms += _ROLL_TAP_SPACING_MS
		elif note.kind == "mine":
			urc_notes.append((head_ms, note.track, NoteType.M))
		elif note.kind == "fake":
			urc_notes.append((head_ms, note.track, NoteType.F))
		else:  # tap | lift
			urc_notes.append((head_ms, note.track, NoteType.N))
	return urc_notes


def _filter_notes(notes: list[SmNote], intervals: list[tuple[int, int]]) -> list[SmNote]:
	kept: list[SmNote] = []
	for note in notes:
		if any(start < note.row < dest for start, dest in intervals):
			continue
		if note.tail_row is not None:
			for start, dest in intervals:
				if start < note.tail_row < dest:
					note.tail_row = start
					break
		kept.append(note)
	return kept


def _warp_intervals(warp_segs: list[tuple[float, float]]) -> list[tuple[int, int]]:
	spans = sorted((_rows(beat), _rows(beat) + _rows(length)) for beat, length in warp_segs)
	merged: list[list[int]] = []
	for start, dest in spans:
		if merged and start < merged[-1][1]:
			if dest > merged[-1][1]:
				merged[-1][1] = dest
		else:
			merged.append([start, dest])
	return [(start, dest) for start, dest in merged]


def _preprocess(
	timing: Timing,
) -> tuple[float, list[tuple[float, float]], list[tuple[float, float]], list[tuple[float, float]]]:
	"""Port of SMLoader::ProcessBPMsAndStops: normalize negative BPMs/stops into warps."""
	bpms = sorted(timing.bpms, key=lambda entry: entry[0])
	sorted_stops = sorted(timing.stops, key=lambda entry: entry[0])

	offset = timing.offset
	stops: list[tuple[float, float]] = []
	for beat, pause in sorted_stops:
		if beat < 0:
			offset -= pause
		else:
			stops.append((beat, pause))

	bpm = 0.0
	index = 0
	while index < len(bpms) and bpms[index][0] <= 0:
		bpm = bpms[index][1]
		index += 1
	if bpm == 0:
		if index == len(bpms):
			raise UrcError("syntax", 1, "no BPM in simfile")
		bpm = bpms[index][1]
		index += 1

	out_bpm: list[tuple[float, float]] = []
	out_stop: list[tuple[float, float]] = []
	out_warp: list[tuple[float, float]] = []
	if 0 < bpm <= _FAST_BPM_WARP:
		out_bpm.append((0.0, bpm))

	prevbeat = 0.0
	timeofs = 0.0
	warpstart = -1.0
	prewarpbpm = 0.0
	ibpm, istop = index, 0
	while ibpm < len(bpms) or istop < len(stops):
		change_is_bpm = istop >= len(stops) or (
			ibpm < len(bpms) and bpms[ibpm][0] <= stops[istop][0]
		)
		beat, value = bpms[ibpm] if change_is_bpm else stops[istop]

		if bpm <= _FAST_BPM_WARP:
			timeofs += (beat - prevbeat) * 60.0 / bpm
			if warpstart >= 0 and bpm > 0 and timeofs > 0:
				warpend = beat - (timeofs * bpm / 60.0)
				out_warp.append((warpstart, warpend - warpstart))
				if bpm != prewarpbpm:
					out_bpm.append((warpstart, bpm))
				warpstart = -1.0
		prevbeat = beat

		if change_is_bpm:
			if warpstart < 0 and (value < 0 or value > _FAST_BPM_WARP):
				warpstart = beat
				prewarpbpm = bpm
				timeofs = 0.0
			elif warpstart < 0:
				out_bpm.append((beat, value))
			bpm = value
			ibpm += 1
		else:
			if warpstart < 0 and value < 0:
				warpstart = beat
				prewarpbpm = bpm
				timeofs = value
			elif warpstart < 0:
				out_stop.append((beat, value))
			else:
				timeofs += value
				if value > 0 and timeofs > 0:
					out_warp.append((warpstart, beat - warpstart))
					out_stop.append((beat, timeofs))
					if bpm < 0 or bpm > _FAST_BPM_WARP:
						warpstart = beat
						timeofs = 0.0
					else:
						if bpm != prewarpbpm:
							out_bpm.append((warpstart, bpm))
						warpstart = -1.0
			istop += 1

	if warpstart >= 0:
		never_ends = bpm < 0 or bpm > _FAST_BPM_WARP
		warpend = 99999999.0 if never_ends else prevbeat - (timeofs * bpm / 60.0)
		out_warp.append((warpstart, warpend - warpstart))
		if bpm != prewarpbpm:
			out_bpm.append((warpstart, bpm))

	return offset, out_bpm, out_stop, out_warp


def _difficulty_name(difficulty: str, description: str) -> str:
	name = _DIFFICULTY_NAMES.get(difficulty.strip().lower(), "")
	if name == "Hard" and description.strip().lower() in ("smaniac", "challenge"):
		name = "Challenge"
	return name or "Edit"


def _rows(beats: float) -> int:
	value = beats * 48.0
	return int(value + 0.5) if value >= 0 else int(value - 0.5)
