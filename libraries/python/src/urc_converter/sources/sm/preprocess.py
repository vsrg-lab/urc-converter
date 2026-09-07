"""SMLoader timing preprocessing and note filtering for StepMania simfiles."""

from ...error import UrcError
from .model import SmNote, Timing

_ROWS_PER_BEAT = 48.0
_FAST_BPM_WARP = 9999999.0


def rows(beats: float) -> int:
	"""Convert beat position to 48th-row index."""
	value = beats * _ROWS_PER_BEAT
	return int(value + 0.5) if value >= 0 else int(value - 0.5)


def warp_intervals(warp_segs: list[tuple[float, float]]) -> list[tuple[int, int]]:
	"""Merge warp segments into [start, dest) row intervals; adopt greater dest."""
	spans = sorted((rows(beat), rows(beat) + rows(length)) for beat, length in warp_segs)
	merged: list[list[int]] = []
	for start, dest in spans:
		if merged and start < merged[-1][1]:
			if dest > merged[-1][1]:
				merged[-1][1] = dest
		else:
			merged.append([start, dest])
	return [(start, dest) for start, dest in merged]


def filter_notes(notes: list[SmNote], intervals: list[tuple[int, int]]) -> list[SmNote]:
	"""Drop notes inside a warp and truncate tails that end inside one to warp start."""
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


def preprocess(
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
