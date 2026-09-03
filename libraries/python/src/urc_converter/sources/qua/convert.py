"""Mapper from the Quaver source model onto a URC chart."""

from ...error import UrcError
from ...model import Chart, Layout, Metadata, Note, NoteType, Version
from .._shared import build_timing, check_hold_overlap, first_downbeat_after
from .model import QuaMap

_MODE_KEYS = {1: 4, 2: 7, 3: 1, 4: 2, 5: 3, 6: 5, 7: 6, 8: 8, 9: 9, 10: 10}


def convert_qua(qua: QuaMap) -> Chart:
	"""Map a Quaver chart onto a URC chart."""
	keys = _MODE_KEYS.get(qua.mode)
	if keys is None:
		raise UrcError("unsupported-version", 1, f"unsupported Quaver mode: {qua.mode}")

	special_keys = 1 if qua.has_scratch_key else 0

	first_note_time = min((obj.start_time for obj in qua.hit_objects), default=0)

	bpm_points = [
		(point.start_time, point.bpm, point.signature, 4) for point in qua.timing_points
	]
	timing = build_timing(
		bpm_points=bpm_points,
		sv_points=[(point.start_time, point.multiplier) for point in qua.sv_points],
		first_note_time=first_note_time,
		source=".qua",
		measure_anchor_ms=first_downbeat_after(bpm_points, first_note_time),
	)

	total = keys + special_keys
	notes: list[Note] = []

	for obj in qua.hit_objects:
		lane = obj.lane - 1
		if not 0 <= lane < total:
			raise UrcError("syntax", 1, f"lane out of range: {obj.lane}")

		if obj.end_time and obj.end_time < obj.start_time:
			raise UrcError(
				"syntax", 1, f"hold ends before it starts: {obj.end_time} < {obj.start_time}"
			)

		if obj.end_time:
			notes.append(
				Note(timestamp_ms=obj.start_time - first_note_time, lane=lane, type=NoteType.LS)
			)
			notes.append(
				Note(timestamp_ms=obj.end_time - first_note_time, lane=lane, type=NoteType.LE)
			)
		elif obj.mine:
			notes.append(
				Note(timestamp_ms=obj.start_time - first_note_time, lane=lane, type=NoteType.M)
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
