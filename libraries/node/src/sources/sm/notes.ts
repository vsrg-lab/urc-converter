/**
 * Note data parsing for StepMania simfiles.
 */
import type { SmNote } from "./model.js";
import { rows } from "./preprocess.js";
import { UrcError } from "../../error.js";

/**
 * Parses raw StepMania note data into SmNote events.
 */
export function parseNoteData(data: string, lanes: number): SmNote[] {
	const notes: SmNote[] = [];
	const openHolds = new Map<number, SmNote>();

	let measure = 0;
	for (const part of data.split(",")) {
		if (part === "")
			continue;
		const content = part
			.split("\n")
			.map(raw => raw.replace(/^[ \t\r]+/, "").replace(/[ \t\r]+$/, ""))
			.filter(line => line !== "");
		for (let index = 0; index < content.length; index++) {
			const line = content[index];
			const row = rows((measure + index / content.length) * 4);
			let track = 0;
			let position = 0;
			while (track < lanes && position < line.length) {
				const char = line[position];
				position++;
				if (char === "1")
					notes.push({ row, track, kind: "tap", tailRow: null });
				else if (char === "2" || char === "4") {
					if (openHolds.has(track))
						throw new UrcError("syntax", 1, `overlapping hold head at row ${row}`);
					const note: SmNote = {
						row,
						track,
						kind: char === "2" ? "hold" : "roll",
						tailRow: null
					};
					notes.push(note);
					openHolds.set(track, note);
				} else if (char === "3") {
					const open = openHolds.get(track);
					if (open === undefined)
						throw new UrcError("syntax", 1, `hold tail without a head at row ${row}`);
					open.tailRow = row;
					openHolds.delete(track);
				} else if (char === "M")
					notes.push({ row, track, kind: "mine", tailRow: null });
				else if (char === "L")
					notes.push({ row, track, kind: "lift", tailRow: null });
				else if (char === "F")
					notes.push({ row, track, kind: "fake", tailRow: null });
				if (position < line.length && line[position] === "[") {
					const end = line.indexOf("]", position);
					position = end < 0 ? line.length : end + 1;
				}
				track++;
			}
		}
		measure++;
	}

	if (openHolds.size > 0)
		throw new UrcError("syntax", 1, "hold note without a tail");
	return notes;
}
