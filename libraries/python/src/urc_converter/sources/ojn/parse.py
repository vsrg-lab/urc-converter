"""Parser for O2Jam (.ojn) chart binaries."""

import math
import struct

from ...error import UrcError
from .model import OjnDifficulty, OjnEvent, OjnFile

_STRING_FIELDS = ((108, 172), (172, 204), (204, 236))
_ENCODINGS = ("utf-8", "cp949")


def parse_ojn(data: bytes) -> OjnFile:
	"""Parse a plain or encrypted (``new`` magic) OJN file."""
	if len(data) >= 8 and data[:3] == b"new" and data[4:8] != b"ojn\x00":
		data = _decrypt(data)
	if len(data) < 300:
		raise UrcError("syntax", len(data), "file too short for OJN header")
	if data[4:8] != b"ojn\x00":
		raise UrcError("syntax", 4, "invalid OJN signature")
	bpm = struct.unpack_from("<f", data, 16)[0]
	if not math.isfinite(bpm) or bpm <= 0:
		raise UrcError("syntax", 16, "header BPM must be positive")

	title, artist, noter = _decode_strings(data)
	package_counts = struct.unpack_from("<3i", data, 64)
	note_offsets = struct.unpack_from("<3i", data, 284)
	cover_offset = struct.unpack_from("<i", data, 296)[0]

	difficulties = []
	for index in range(3):
		count = package_counts[index]
		if count < 0:
			raise UrcError("syntax", 64 + index * 4, "negative package count")
		if count == 0:
			continue
		start = note_offsets[index]
		end = _section_end(start, note_offsets, cover_offset, len(data))
		difficulties.append(_read_difficulty(data, index, start, end, count))
	if not difficulties:
		raise UrcError("syntax", 0, "no difficulty with note packages")
	return OjnFile(title=title, artist=artist, noter=noter, bpm=bpm, difficulties=difficulties)


def _section_end(
	start: int, note_offsets: tuple[int, int, int], cover_offset: int, file_len: int
) -> int:
	candidates = [off for off in (*note_offsets, cover_offset) if off > start]
	bound = min(candidates) if candidates else file_len
	return min(bound, file_len)


def _decrypt(data: bytes) -> bytes:
	block = data[3]
	if block == 0:
		return data
	key = [data[4]] * block
	key[0] = data[6]
	key[block // 2] = data[5]
	size = len(data)
	return bytes(data[size - 1 - i] ^ key[i % block] for i in range(size - 8))


def _read_difficulty(
	data: bytes, index: int, start: int, end: int, count: int
) -> OjnDifficulty:
	if start < 300:
		raise UrcError("syntax", start, "note section overlaps the header")
	events: list[OjnEvent] = []
	offset = start
	for _ in range(count):
		if offset + 8 > end:
			raise UrcError("syntax", offset, "note section truncated")
		measure, channel, total = struct.unpack_from("<iHH", data, offset)
		if measure < 0:
			raise UrcError("syntax", offset, "negative measure index")
		offset += 8
		for i in range(total):
			position = i / total
			if offset + 4 > end:
				raise UrcError("syntax", offset, "note section truncated")
			if channel in (0, 1):
				value = struct.unpack_from("<f", data, offset)[0]
				offset += 4
				if not math.isfinite(value) or value <= 0:
					continue
				events.append(OjnEvent(measure, position, channel, value, 0, offset - 4))
			else:
				value, _volume_pan, type_ = struct.unpack_from("<hBB", data, offset)
				offset += 4
				if channel >= 9 or value == 0 or type_ % 4 == 1:
					continue
				events.append(OjnEvent(measure, position, channel, 0.0, type_ % 4, offset - 4))
	return OjnDifficulty(index=index, events=events)


def _decode_strings(data: bytes) -> tuple[str, str, str]:
	decoded = [_decode_field(data[start:end].split(b"\x00", 1)[0]) for start, end in _STRING_FIELDS]
	return decoded[0], decoded[1], decoded[2]


def _decode_field(field: bytes) -> str:
	if all(byte < 0x80 for byte in field):
		return field.decode("ascii")
	for charset in _ENCODINGS:
		try:
			return field.decode(charset)
		except UnicodeDecodeError:
			continue
	raise UrcError("syntax", 108, "undecodable OJN metadata string")
