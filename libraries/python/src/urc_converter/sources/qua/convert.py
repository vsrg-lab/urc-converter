"""Mapper from the Quaver source model onto a URC chart."""

from ...error import UrcError
from ...model import Chart, Layout, Metadata, Note, NoteType, Version
from ...strings import ZERO_LENGTH_HOLD
from .._shared import build_timing, check_hold_overlap, first_downbeat_after
from .model import QuaHitObject, QuaMap, QuaTimingPoint

_MODE_KEYS = {1: 4, 2: 7, 3: 1, 4: 2, 5: 3, 6: 5, 7: 6, 8: 8, 9: 9, 10: 10}


def convert_qua(qua: QuaMap) -> Chart:
	"""Map a Quaver chart onto a URC chart."""
	keys = _MODE_KEYS.get(qua.mode)
	if keys is None:
		raise UrcError("unsupported-version", 1, f"unsupported Quaver mode: {qua.mode}")

	special_keys = 1 if qua.has_scratch_key else 0
	first_note_time = min((obj.start_time for obj in qua.hit_objects), default=0)

	bpm_points, sv_points, anchor_points = _resolve_timing(qua, first_note_time)
	timing = build_timing(
		bpm_points=bpm_points,
		sv_points=sv_points,
		first_note_time=first_note_time,
		source=".qua",
		measure_anchor_ms=first_downbeat_after(anchor_points, first_note_time),
	)

	total = keys + special_keys
	notes: list[Note] = []

	for obj in qua.hit_objects:
		lane = obj.lane - 1
		if not 0 <= lane < total:
			raise UrcError("syntax", 1, f"lane out of range: {obj.lane}")

		if obj.mine:
			notes.append(
				Note(timestamp_ms=obj.start_time - first_note_time, lane=lane, type=NoteType.M)
			)
		elif obj.end_time != 0:
			if obj.end_time < obj.start_time:
				raise UrcError(
					"syntax", 1, f"hold ends before it starts: {obj.end_time} < {obj.start_time}"
				)
			if obj.end_time == obj.start_time:
				raise UrcError(
					ZERO_LENGTH_HOLD,
					1,
					f"zero-length hold on lane {lane} at {obj.start_time}ms",
				)
			notes.append(
				Note(timestamp_ms=obj.start_time - first_note_time, lane=lane, type=NoteType.LS)
			)
			notes.append(
				Note(timestamp_ms=obj.end_time - first_note_time, lane=lane, type=NoteType.LE)
			)
		else:
			notes.append(
				Note(timestamp_ms=obj.start_time - first_note_time, lane=lane, type=NoteType.N)
			)

	check_hold_overlap(notes)

	missing = [
		label
		for label, value in (
			("Title", qua.title),
			("Artist", qua.artist),
			("Creator", qua.creator),
			("DifficultyName", qua.difficulty_name),
		)
		if not value
	]
	if missing:
		raise UrcError("syntax", 1, f"missing metadata: {', '.join(missing)}")

	return Chart(
		format_version=Version(major=1, minor=1),
		metadata=Metadata(
			original="Quaver",
			title=qua.title,
			artist=qua.artist,
			creator=qua.creator,
			version=qua.difficulty_name,
		),
		judgment=None,
		layout=Layout(
			keys=keys,
			special_keys=special_keys,
			special_lanes=[keys] if qua.has_scratch_key else None,
		),
		timing=timing,
		notes=notes,
	)


def _common_bpm(nonzero_tps: list[QuaTimingPoint], hit_objects: list[QuaHitObject]) -> float:
	"""Most common positive BPM by duration, matching Quaver.API Qua.GetCommonBpm."""
	if not hit_objects:
		return abs(nonzero_tps[0].bpm)

	last_time = max(
		obj.end_time if not obj.mine and obj.end_time > 0 else obj.start_time
		for obj in hit_objects
	)
	durations: dict[float, int] = {}
	for index in range(len(nonzero_tps) - 1, -1, -1):
		point = nonzero_tps[index]
		if point.start_time > last_time:
			continue
		duration = last_time - (0 if index == 0 else point.start_time)
		last_time = point.start_time
		bpm = abs(point.bpm)
		durations[bpm] = durations.get(bpm, 0) + duration

	if not durations:
		return abs(nonzero_tps[0].bpm)

	best_bpm = abs(nonzero_tps[0].bpm)
	best_duration = -1
	for bpm, duration in durations.items():
		if duration > best_duration:
			best_duration = duration
			best_bpm = bpm
	return best_bpm


def _resolve_timing(
	qua: QuaMap, first_note_time: int
) -> tuple[
	list[tuple[int, float, int, int]],
	list[tuple[int, float]],
	list[tuple[int, float, int, int]],
]:
	if not qua.timing_points:
		raise UrcError("syntax", 1, ".qua: no BPM timing point")

	sorted_tps = sorted(qua.timing_points, key=lambda point: point.start_time)
	sorted_svs = sorted(qua.sv_points, key=lambda point: point.start_time)

	# Keep the last timing point / SV at each timestamp.
	tp_by_time: dict[int, QuaTimingPoint] = {}
	for tp in sorted_tps:
		tp_by_time[tp.start_time] = tp
	sv_by_time: dict[int, float] = {}
	for sv in sorted_svs:
		sv_by_time[sv.start_time] = sv.multiplier

	dedup_tps = list(tp_by_time.values())
	nonzero_tps = [tp for tp in dedup_tps if tp.bpm != 0.0]
	if not nonzero_tps:
		raise UrcError("syntax", 1, ".qua: no positive BPM timing point")

	fallback_bpm = abs(nonzero_tps[0].bpm)
	base_bpm = _common_bpm(nonzero_tps, qua.hit_objects) if qua.bpm_does_not_affect_sv else 0.0

	first_tp = dedup_tps[0]
	eff_bpm = abs(first_tp.bpm) if first_tp.bpm != 0.0 else fallback_bpm
	bpm_factor = 1.0 if first_tp.bpm > 0.0 else (-1.0 if first_tp.bpm < 0.0 else 0.0)
	beats = first_tp.signature
	raw_sv = qua.initial_scroll_velocity if qua.bpm_does_not_affect_sv else 1.0

	all_times = sorted(set(tp_by_time) | set(sv_by_time))
	bpm_points: list[tuple[int, float, int, int]] = []
	sv_points: list[tuple[int, float]] = []

	if qua.bpm_does_not_affect_sv and all_times[0] > first_note_time:
		init_mult = raw_sv if eff_bpm == base_bpm else raw_sv * (base_bpm / eff_bpm)
		if init_mult == 0.0:
			init_mult = 0.0
		if init_mult != 1.0:
			bpm_points.append((first_note_time, eff_bpm, beats, 4))
			sv_points.append((first_note_time, init_mult))

	for time in all_times:
		tp = tp_by_time.get(time)
		has_sv = time in sv_by_time
		if has_sv:
			raw_sv = sv_by_time[time]
		elif tp is not None and not qua.bpm_does_not_affect_sv:
			raw_sv = 1.0

		if tp is not None:
			beats = tp.signature
			if tp.bpm > 0.0:
				eff_bpm = tp.bpm
				bpm_factor = 1.0
			elif tp.bpm < 0.0:
				eff_bpm = abs(tp.bpm)
				bpm_factor = -1.0
			else:
				bpm_factor = 0.0

		if qua.bpm_does_not_affect_sv:
			eff_mult = raw_sv if eff_bpm == base_bpm else raw_sv * (base_bpm / eff_bpm)
		else:
			eff_mult = raw_sv * bpm_factor

		if eff_mult == 0.0:
			eff_mult = 0.0

		bpm_points.append((time, eff_bpm, beats, 4))
		sv_points.append((time, eff_mult))

	anchor_points = [(tp.start_time, abs(tp.bpm), tp.signature, 4) for tp in nonzero_tps]
	return bpm_points, sv_points, anchor_points
