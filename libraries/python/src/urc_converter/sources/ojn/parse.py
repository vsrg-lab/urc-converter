"""Parser for O2Jam (.ojn) chart binaries."""

import struct

from ...error import UrcError
from .model import OjnDifficulty, OjnEvent, OjnFile

_STRING_FIELDS = ((108, 172), (172, 204), (204, 236), (236, 268))


def parse_ojn(data: bytes) -> OjnFile:
	"""Parse a plain or encrypted (``new`` magic) OJN file."""
	if data[:3] == b"new" and len(data) >= 8:
		data = _decrypt(data)
	if len(data) < 300:
		raise UrcError("syntax", len(data), "file too short for OJN header")
	if data[4:8] != b"ojn\x00":
		raise UrcError("syntax", 4, "invalid OJN signature")
	bpm = struct.unpack_from("<f", data, 16)[0]
	if bpm <= 0:
		raise UrcError("syntax", 16, "header BPM must be positive")

	title, artist, noter, _ojm = _decode_strings(data)
	package_counts = struct.unpack_from("<3i", data, 64)
	note_offsets = struct.unpack_from("<3i", data, 284)
	cover_offset = struct.unpack_from("<i", data, 296)[0]

	difficulties = []
	for index in range(3):
		if package_counts[index] == 0:
			continue
		start = note_offsets[index]
		end = note_offsets[index + 1] if index < 2 else cover_offset
		difficulties.append(
			_read_difficulty(data, index, start, min(end, len(data)), package_counts[index])
		)
	if not difficulties:
		raise UrcError("syntax", 0, "no difficulty with note packages")
	return OjnFile(title=title, artist=artist, noter=noter, bpm=bpm, difficulties=difficulties)


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
		measure, channel, total = struct.unpack_from("<iHh", data, offset)
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
				if value <= 0:
					continue
				events.append(OjnEvent(measure, position, channel, value, 0, offset - 4))
			else:
				value, _volume_pan, type_ = struct.unpack_from("<hBB", data, offset)
				offset += 4
				if channel >= 9 or value == 0 or type_ % 4 == 1:
					continue
				events.append(OjnEvent(measure, position, channel, 0.0, type_ % 4, offset - 4))
	return OjnDifficulty(index=index, events=events)


def _decode_strings(data: bytes) -> tuple[str, str, str, str]:
	fields = [data[start:end].split(b"\x00", 1)[0] for start, end in _STRING_FIELDS]
	blob = b"".join(fields)
	if all(byte < 0x80 for byte in blob):
		charset = "ascii"
	elif _decodes(blob, "utf-8"):
		charset = "utf-8"
	elif _decodes(blob, "cp949"):
		charset = "cp949"
	else:
		raise UrcError("syntax", 108, "strings are neither ASCII, UTF-8, nor CP949")
	try:
		decoded = [field.decode(charset) for field in fields]
	except UnicodeDecodeError:
		raise UrcError("syntax", 108, "invalid string byte sequence") from None
	return decoded[0], decoded[1], decoded[2], decoded[3]


def _decodes(blob: bytes, charset: str) -> bool:
	try:
		blob.decode(charset)
	except UnicodeDecodeError:
		return False
	return True
