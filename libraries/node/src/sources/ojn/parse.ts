/**
 * Parser for O2Jam (.ojn) chart binaries.
 */
import { UrcError } from "../../error.js";
import type { OjnDifficulty, OjnEvent, OjnFile } from "./model.js";

const STRING_RANGES: ReadonlyArray<readonly [number, number]> = [
	[108, 172],
	[172, 204],
	[204, 236]
];

const ENCODINGS = ["utf-8", "euc-kr"] as const;

/**
 * Parses a plain or encrypted ("new" magic) OJN file into its source model.
 */
export function parseOjn(bytes: Uint8Array): OjnFile {
	const data =
		bytes.length >= 8
		&& String.fromCharCode(...bytes.subarray(0, 3)) === "new"
		&& String.fromCharCode(...bytes.subarray(4, 8)) !== "ojn\x00"
			? decrypt(bytes)
			: bytes;
	if (data.length < 300)
		throw new UrcError("syntax", data.length, "file too short for OJN header");
	if (String.fromCharCode(...data.subarray(4, 8)) !== "ojn\x00")
		throw new UrcError("syntax", 4, "invalid OJN signature");
	const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
	const bpm = view.getFloat32(16, true);
	if (!Number.isFinite(bpm) || bpm <= 0)
		throw new UrcError("syntax", 16, "header BPM must be positive");

	const [title, artist, noter] = decodeStrings(data);
	const difficulties: OjnDifficulty[] = [];
	const packageCounts = [64, 68, 72].map(offset => view.getInt32(offset, true));
	const noteOffsets = [284, 288, 292].map(offset => view.getInt32(offset, true));
	const coverOffset = view.getInt32(296, true);
	for (let index = 0; index < 3; index++) {
		const count = packageCounts[index];
		if (count < 0)
			throw new UrcError("syntax", 64 + index * 4, "negative package count");
		if (count === 0)
			continue;
		const start = noteOffsets[index];
		const end = sectionEnd(start, noteOffsets, coverOffset, data.length);
		difficulties.push(readDifficulty(view, index, start, end, count));
	}
	if (difficulties.length === 0)
		throw new UrcError("syntax", 0, "no difficulty with note packages");
	return { title, artist, noter, bpm, difficulties };
}

function sectionEnd(start: number, noteOffsets: number[], coverOffset: number, fileLen: number): number {
	let bound = fileLen;
	for (const candidate of [...noteOffsets, coverOffset])
		if (candidate > start && candidate < bound)
			bound = candidate;
	return Math.min(bound, fileLen);
}

function decrypt(bytes: Uint8Array): Uint8Array {
	const block = bytes[3];
	if (block === 0)
		return bytes;
	const key = new Array<number>(block).fill(bytes[4]);
	key[0] = bytes[6];
	key[block >> 1] = bytes[5];
	const size = bytes.length;
	const plain = new Uint8Array(size - 8);
	for (let i = 0; i < size - 8; i++)
		plain[i] = bytes[size - 1 - i] ^ key[i % block];
	return plain;
}

function readDifficulty(
	view: DataView,
	index: number,
	start: number,
	end: number,
	count: number
): OjnDifficulty {
	if (start < 300)
		throw new UrcError("syntax", start, "note section overlaps the header");
	const events: OjnEvent[] = [];
	let offset = start;
	for (let packageIndex = 0; packageIndex < count; packageIndex++) {
		if (offset + 8 > end)
			throw new UrcError("syntax", offset, "note section truncated");
		const measure = view.getInt32(offset, true);
		const channel = view.getUint16(offset + 4, true);
		const total = view.getUint16(offset + 6, true);
		if (measure < 0)
			throw new UrcError("syntax", offset, "negative measure index");
		offset += 8;
		for (let i = 0; i < total; i++) {
			const position = i / total;
			if (offset + 4 > end)
				throw new UrcError("syntax", offset, "note section truncated");
			if (channel <= 1) {
				const value = view.getFloat32(offset, true);
				offset += 4;
				if (!Number.isFinite(value) || value <= 0)
					continue;
				events.push({ measure, position, channel, value, kind: 0, offset: offset - 4 });
			} else {
				const value = view.getInt16(offset, true);
				const noteType = view.getUint8(offset + 3);
				offset += 4;
				if (channel >= 9 || value === 0 || noteType % 4 === 1)
					continue;
				events.push({ measure, position, channel, value: 0, kind: noteType % 4, offset: offset - 4 });
			}
		}
	}
	return { index, events };
}

function decodeStrings(data: Uint8Array): [string, string, string] {
	const decoded = STRING_RANGES.map(([start, end]) => {
		const field = data.subarray(start, end);
		const nul = field.indexOf(0);
		return decodeField(nul === -1 ? field : field.subarray(0, nul));
	});
	return [decoded[0], decoded[1], decoded[2]];
}

function decodeField(field: Uint8Array): string {
	for (const label of ENCODINGS)
		try {
			return new TextDecoder(label, { fatal: true }).decode(field);
		} catch {
			continue;
		}

	throw new UrcError("syntax", 108, "undecodable OJN metadata string");
}
