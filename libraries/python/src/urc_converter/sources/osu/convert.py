"""Mapper from the osu!mania source model onto a URC chart."""

import math

from ...error import UrcError
from ...model import Chart, Judgment, Layout, Metadata, Note, NoteType, Version
from ...strings import ZERO_LENGTH_HOLD
from .._shared import build_timing, check_hold_overlap, first_downbeat_after
from .model import OsuBeatmap

_JUDGMENT_RATES = [100.0, 100.0, 66.67, 33.33, 16.67, 0.0]
_KEY_MIN, _KEY_MAX = 1, 18
_TYPE_ORDER = {NoteType.N: 0, NoteType.LS: 1, NoteType.LE: 2, NoteType.M: 3, NoteType.F: 4}


def convert_osu(beatmap: OsuBeatmap) -> Chart:
	"""Map an osu!mania beatmap onto a URC chart."""
	if beatmap.mode != 3:
		raise UrcError("unsupported-version", 1, f"unsupported game mode: {beatmap.mode}")

	if beatmap.circle_size is None:
		raise UrcError("syntax", 1, "missing CircleSize")

	if not math.isfinite(beatmap.circle_size) or beatmap.circle_size != math.trunc(
		beatmap.circle_size
	):
		raise UrcError("syntax", 1, f"CircleSize must be an integer: {beatmap.circle_size}")

	keys = int(beatmap.circle_size)
	if not _KEY_MIN <= keys <= _KEY_MAX:
		raise UrcError("syntax", 1, f"CircleSize out of range: {keys}")

	first_note_time = min((obj.time for obj in beatmap.hit_objects), default=0)

	bpm_points, sv_points, anchor_points = _resolve_timing(beatmap)
	timing = build_timing(
		bpm_points=bpm_points,
		sv_points=sv_points,
		first_note_time=first_note_time,
		source=".osu",
		measure_anchor_ms=first_downbeat_after(anchor_points, first_note_time),
	)

	notes: list[Note] = []
	for obj in beatmap.hit_objects:
		lane = min(max(math.floor(obj.x * keys / 512), 0), keys - 1)

		if obj.is_hold:
			if obj.end_time is None or obj.end_time < obj.time:
				raise UrcError(
					"syntax", 1, f"hold ends before it starts: {obj.end_time} < {obj.time}"
				)
			if obj.end_time == obj.time:
				raise UrcError(
					ZERO_LENGTH_HOLD,
					1,
					f"zero-length hold on lane {lane} at {obj.time}ms",
				)
			notes.append(Note(timestamp_ms=obj.time - first_note_time, lane=lane, type=NoteType.LS))
			notes.append(
				Note(timestamp_ms=obj.end_time - first_note_time, lane=lane, type=NoteType.LE)
			)
		else:
			notes.append(Note(timestamp_ms=obj.time - first_note_time, lane=lane, type=NoteType.N))

	notes.sort(key=lambda note: (note.timestamp_ms, note.lane, _TYPE_ORDER[note.type]))
	check_hold_overlap(notes)

	judgment = None
	if beatmap.overall_difficulty is not None:
		od = beatmap.overall_difficulty
		windows = [16.5]
		for base in (64, 97, 127, 151, 188):
			windows.append(max(windows[-1], base - 3 * od + 0.5))
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


def _resolve_timing(
	beatmap: OsuBeatmap,
) -> tuple[
	list[tuple[int, float, int, int]],
	list[tuple[int, float]],
	list[tuple[int, float, int, int]],
]:
	tp_by_time: dict[int, tuple[float, int]] = {}
	sv_by_time: dict[int, float] = {}

	for point in beatmap.timing_points:
		if not math.isfinite(point.beat_length) or point.beat_length == 0:
			raise UrcError("syntax", 1, "timing point with zero beat length")
		is_sv = (not point.uninherited) or (point.beat_length < 0)
		if is_sv:
			if point.beat_length > 0:
				raise UrcError("syntax", 1, "inherited timing point with positive beat length")
			sv_by_time[point.time] = -100.0 / point.beat_length
		else:
			meter = point.meter if point.meter > 0 else 4
			tp_by_time[point.time] = (60000.0 / point.beat_length, meter)

	if not tp_by_time:
		raise UrcError("syntax", 1, ".osu: no BPM timing point")

	all_times = sorted(set(tp_by_time) | set(sv_by_time))
	bpm_points: list[tuple[int, float, int, int]] = []
	sv_points: list[tuple[int, float]] = []
	anchor_points = [
		(time, bpm, meter, 4) for time, (bpm, meter) in sorted(tp_by_time.items())
	]

	current_bpm: float | None = None
	current_meter = 4
	current_sv = 1.0

	for time in all_times:
		tp = tp_by_time.get(time)
		has_sv = time in sv_by_time
		if has_sv:
			current_sv = sv_by_time[time]
		elif tp is not None:
			current_sv = 1.0

		if tp is not None:
			current_bpm, current_meter = tp

		if current_bpm is None:
			continue

		bpm_points.append((time, current_bpm, current_meter, 4))
		sv_points.append((time, current_sv))

	return bpm_points, sv_points, anchor_points
