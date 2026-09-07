/**
 * Parser for O2Jam (.ojn) chart binaries.
 */
import type { OjnDifficulty, OjnEvent, OjnFile } from "./model.js";
import { UrcError } from "../../error.js";

const STRING_RANGES: ReadonlyArray<readonly [number, number]> = [
	[108, 172],
	[172, 204],
	[204, 236],
	[236, 268]
];

/**
 * Parses a plain or encrypted ("new" magic) OJN file into its source model.
 */
export function parseOjn(bytes: Uint8Array): OjnFile {
	const data =
		bytes.length >= 8 && String.fromCharCode(...bytes.subarray(0, 3)) === "new"
			? decrypt(bytes)
			: bytes;
	if (data.length < 300)
		throw new UrcError("syntax", data.length, "file too short for OJN header");
	if (String.fromCharCode(...data.subarray(4, 8)) !== "ojn\x00")
		throw new UrcError("syntax", 4, "invalid OJN signature");
	const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
	const bpm = view.getFloat32(16, true);
	if (bpm <= 0)
		throw new UrcError("syntax", 16, "header BPM must be positive");

	const [title, artist, noter] = decodeStrings(data);
	const difficulties: OjnDifficulty[] = [];
	const packageCounts = [64, 68, 72].map(offset => view.getInt32(offset, true));
	const noteOffsets = [284, 288, 292].map(offset => view.getInt32(offset, true));
	const coverOffset = view.getInt32(296, true);
	for (let index = 0; index < 3; index++) {
		if (packageCounts[index] === 0)
			continue;
		const endRaw = index < 2 ? noteOffsets[index + 1] : coverOffset;
		difficulties.push(
			readDifficulty(view, index, noteOffsets[index], Math.min(Math.max(endRaw, 0), data.length), packageCounts[index])
		);
	}
	if (difficulties.length === 0)
		throw new UrcError("syntax", 0, "no difficulty with note packages");
	return { title, artist, noter, bpm, difficulties };
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
		const total = view.getInt16(offset + 6, true);
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
				if (value <= 0)
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
	const fields = STRING_RANGES.map(([start, end]) => {
		const field = data.subarray(start, end);
		const nul = field.indexOf(0);
		return nul === -1 ? field : field.subarray(0, nul);
	});
	const blobLength = fields.reduce((sum, field) => sum + field.length, 0);
	const blob = new Uint8Array(blobLength);
	let cursor = 0;
	for (const field of fields) {
		blob.set(field, cursor);
		cursor += field.length;
	}
	const label = blob.every(byte => byte < 0x80)
		? "utf-8"
		: decodesAs(blob, "utf-8")
			? "utf-8"
			: decodesAs(blob, "euc-kr")
				? "euc-kr"
				: null;
	if (label === null)
		throw new UrcError("syntax", 108, "strings are neither ASCII, UTF-8, nor CP949");
	const decoder = new TextDecoder(label, { fatal: true });
	try {
		return [decoder.decode(fields[0]), decoder.decode(fields[1]), decoder.decode(fields[2])];
	} catch {
		throw new UrcError("syntax", 108, "invalid string byte sequence");
	}
}

function decodesAs(bytes: Uint8Array, label: string): boolean {
	try {
		new TextDecoder(label, { fatal: true }).decode(bytes);
		return true;
	} catch {
		return false;
	}
}
