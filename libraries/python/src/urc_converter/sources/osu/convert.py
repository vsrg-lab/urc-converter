"""Mapper from the osu!mania source model onto a URC chart."""

import math

from ...error import UrcError
from ...model import Chart, Judgment, Layout, Metadata, Note, NoteType, Version
from .._shared import build_timing, check_hold_overlap, first_downbeat_after
from .model import OsuBeatmap

_JUDGMENT_RATES = [100.0, 100.0, 66.67, 33.33, 16.67, 0.0]
_KEY_MIN, _KEY_MAX = 1, 18


def convert_osu(beatmap: OsuBeatmap) -> Chart:
	"""Map an osu!mania beatmap onto a URC chart."""
	if beatmap.mode != 3:
		raise UrcError("unsupported-version", 1, f"unsupported game mode: {beatmap.mode}")

	if beatmap.circle_size is None:
		raise UrcError("syntax", 1, "missing CircleSize")

	if beatmap.circle_size != int(beatmap.circle_size):
		raise UrcError("syntax", 1, f"CircleSize must be an integer: {beatmap.circle_size}")

	keys = int(beatmap.circle_size)
	if not _KEY_MIN <= keys <= _KEY_MAX:
		raise UrcError("syntax", 1, f"CircleSize out of range: {keys}")

	if any(point.beat_length == 0 for point in beatmap.timing_points):
		raise UrcError("syntax", 1, "timing point with zero beat length")

	first_note_time = min((obj.time for obj in beatmap.hit_objects), default=0)

	bpm_points = [
		(point.time, 60000.0 / point.beat_length, point.meter, 4)
		for point in beatmap.timing_points
		if point.uninherited
	]
	timing = build_timing(
		bpm_points=bpm_points,
		sv_points=[
			(point.time, -100.0 / point.beat_length)
			for point in beatmap.timing_points
			if not point.uninherited
		],
		first_note_time=first_note_time,
		source=".osu",
		measure_anchor_ms=first_downbeat_after(bpm_points, first_note_time),
	)

	notes: list[Note] = []
	for obj in beatmap.hit_objects:
		lane = min(max(math.floor(obj.x * keys / 512), 0), keys - 1)

		if obj.is_hold:
			if obj.end_time < obj.time:
				raise UrcError(
					"syntax", 1, f"hold ends before it starts: {obj.end_time} < {obj.time}"
				)
			notes.append(Note(timestamp_ms=obj.time - first_note_time, lane=lane, type=NoteType.LS))
			notes.append(
				Note(timestamp_ms=obj.end_time - first_note_time, lane=lane, type=NoteType.LE)
			)
		else:
			notes.append(Note(timestamp_ms=obj.time - first_note_time, lane=lane, type=NoteType.N))

	check_hold_overlap(notes)

	judgment = None
	if beatmap.overall_difficulty is not None:
		od = beatmap.overall_difficulty
		windows = [16.5] + [base - 3 * od + 0.5 for base in (64, 97, 127, 151, 188)]
		judgment = Judgment(windows=windows, rates=list(_JUDGMENT_RATES))

	title = beatmap.title_unicode or beatmap.title
	artist = beatmap.artist_unicode or beatmap.artist
	missing = [
		label
		for label, value in (
			("Title", title),
			("Artist", artist),
			("Creator", beatmap.creator),
			("Version", beatmap.version),
		)
		if not value
	]
	if missing:
		raise UrcError("syntax", 1, f"missing metadata: {', '.join(missing)}")

	return Chart(
		format_version=Version(major=1, minor=1),
		metadata=Metadata(
			original="osu!mania",
			title=title,
			artist=artist,
			creator=beatmap.creator,
			version=beatmap.version,
		),
		judgment=judgment,
		layout=Layout(keys=keys, special_keys=0, special_lanes=None),
		timing=timing,
		notes=notes,
	)
