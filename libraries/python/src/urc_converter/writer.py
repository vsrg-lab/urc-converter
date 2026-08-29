"""Canonical URC serializer."""

from .model import Chart, Judgment
from .strings import (
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
)


def _float(value: float) -> str:
	text = repr(value)
	if text.endswith(".0"):
		text = text[:-2]
	return text


def _judgment_lines(judgment: Judgment) -> list[str]:
	windows = ", ".join(_float(value) for value in judgment.windows)
	rates = ", ".join(_float(value) for value in judgment.rates)
	return [f"{JUDGMENT_FIELD_WINDOW}: {windows}", f"{JUDGMENT_FIELD_RATE}: {rates}"]


def write(chart: Chart) -> str:
	"""Serialize a Chart to canonical URC text."""
	layout = chart.layout
	if layout.special_keys > 0:
		type_text = f"{layout.keys}+{layout.special_keys}"
	else:
		type_text = str(layout.keys)
	if layout.special_lanes is None:
		special_text = "None"
	else:
		special_text = ", ".join(str(lane) for lane in layout.special_lanes)
	metadata = chart.metadata
	metadata_values = (
		metadata.original,
		metadata.title,
		metadata.artist,
		metadata.creator,
		metadata.version,
	)
	lines = [f"@URC {chart.format_version.major}.{chart.format_version.minor}", ""]
	lines.append(SECTION_METADATA)
	for name, value in zip(METADATA_FIELDS, metadata_values, strict=True):
		lines.append(f"{name}: {value}")
	if chart.judgment is not None:
		lines.append("")
		lines.append(SECTION_JUDGMENT)
		lines.extend(_judgment_lines(chart.judgment))
	lines.append("")
	lines.append(SECTION_LAYOUT)
	lines.append(f"{LAYOUT_FIELD_TYPE}: {type_text}")
	lines.append(f"{LAYOUT_FIELD_SPECIAL}: {special_text}")
	lines.append("")
	lines.append(SECTION_TIMING)
	for point in chart.timing:
		fields = [
			str(point.timestamp_ms),
			_float(point.bpm),
			f"{point.meter.beats}/{point.meter.note_value}",
		]
		if point.multiplier is not None:
			fields.append(_float(point.multiplier))
		lines.append(", ".join(fields))
	lines.append("")
	lines.append(SECTION_NOTES)
	for note in sorted(chart.notes, key=lambda item: (item.timestamp_ms, item.lane)):
		lines.append(f"{note.timestamp_ms}, {note.lane}, {note.type.value}")
	return "\n".join(lines) + "\n"
