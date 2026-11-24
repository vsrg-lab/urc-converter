import type { Chart, Judgment } from "./model.js";
import {
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
	SPECIAL_NONE
} from "./strings.js";

/**
 * Formats a float as the shortest round-trip plain decimal without
 * scientific notation or a forced fraction part (`16.5`, `222.22`, `120`).
 */
function formatFloat(value: number): string {
	if (value === 0)
		return "0";
	const text = String(value);
	const eIdx = text.search(/[eE]/);
	if (eIdx === -1)
		return text;
	const neg = text.startsWith("-");
	const mantissa = text.slice(neg ? 1 : 0, eIdx);
	const exp = Number(text.slice(eIdx + 1));
	const dotIdx = mantissa.indexOf(".");
	const digits = dotIdx === -1
		? mantissa
		: mantissa.slice(0, dotIdx) + mantissa.slice(dotIdx + 1);
	const fracLen = dotIdx === -1 ? 0 : mantissa.length - dotIdx - 1;
	const point = digits.length - fracLen + exp;
	let body: string;
	if (point <= 0)
		body = `0.${"0".repeat(-point)}${digits}`;
	else if (point >= digits.length)
		body = digits + "0".repeat(point - digits.length);
	else
		body = `${digits.slice(0, point)}.${digits.slice(point)}`;
	return neg ? `-${body}` : body;
}

function joinFloats(values: number[]): string {
	return values.map(formatFloat).join(", ");
}

function judgmentLines(judgment: Judgment): string[] {
	return [
		`${JUDGMENT_FIELD_WINDOW}: ${joinFloats(judgment.windows)}`,
		`${JUDGMENT_FIELD_RATE}: ${joinFloats(judgment.rates)}`
	];
}

/**
 * Serializes a Chart to canonical URC text.
 */
export function write(chart: Chart): string {
	const { layout } = chart;
	const typeText = layout.specialKeys > 0
		? `${layout.keys}+${layout.specialKeys}`
		: String(layout.keys);
	const specialText = layout.specialLanes === null
		? SPECIAL_NONE
		: layout.specialLanes.map(lane => String(lane)).join(", ");
	const { metadata } = chart;
	const metadataValues = [metadata.original, metadata.title, metadata.artist, metadata.creator, metadata.version];
	const lines: string[] = [`@URC ${chart.formatVersion.major}.${chart.formatVersion.minor}`, "", SECTION_METADATA];
	METADATA_FIELDS.forEach((name, index) => {
		lines.push(`${name}: ${metadataValues[index]}`);
	});
	if (chart.judgment !== null) 
		lines.push("", SECTION_JUDGMENT, ...judgmentLines(chart.judgment));
	
	lines.push("", SECTION_LAYOUT, `${LAYOUT_FIELD_TYPE}: ${typeText}`, `${LAYOUT_FIELD_SPECIAL}: ${specialText}`, "", SECTION_TIMING);
	for (const point of chart.timing) {
		const fields = [
			String(point.timestampMs),
			formatFloat(point.bpm),
			`${point.meter.beats}/${point.meter.noteValue}`
		];
		if (point.multiplier !== null) 
			fields.push(formatFloat(point.multiplier));
		
		lines.push(fields.join(", "));
	}
	lines.push("", SECTION_NOTES);
	const ordered = [...chart.notes].sort((a, b) => a.timestampMs - b.timestampMs || a.lane - b.lane);
	for (const note of ordered) 
		lines.push(`${note.timestampMs}, ${note.lane}, ${note.type}`);
	
	return `${lines.join("\n")}\n`;
}
