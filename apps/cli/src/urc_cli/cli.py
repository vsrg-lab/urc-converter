"""Subcommands of the urc CLI."""

import argparse
import sys
from collections.abc import Sequence
from pathlib import Path

from urc_converter import NoteType, UrcError, parse, write
from urc_converter.sources import convert_osu, convert_qua, parse_osu, parse_qua


def command_info(path: Path) -> int:
	try:
		chart = parse(path.read_text(encoding="utf-8"))
	except UrcError as error:
		print(f"error: {path}: {error}", file=sys.stderr)
		return 1

	counts = {note_type: 0 for note_type in NoteType}
	for note in chart.notes:
		counts[note.type] += 1

	special = f" +{chart.layout.special_keys} special" if chart.layout.special_keys else ""
	note_tokens = ", ".join(f"{note_type.value}:{count}" for note_type, count in counts.items())

	print(f"Version:  {chart.format_version.major}.{chart.format_version.minor}")
	print(f"Title:    {chart.metadata.title}")
	print(f"Artist:   {chart.metadata.artist}")
	print(f"Creator:  {chart.metadata.creator}")
	print(f"Chart:    {chart.metadata.version}")
	print(f"Original: {chart.metadata.original}")
	print(f"Layout:   {chart.layout.keys} keys{special}")
	print(f"Timing:   {len(chart.timing)} points")
	print(f"Notes:    {len(chart.notes)} ({note_tokens})")

	return 0


def command_validate(path: Path) -> int:
	try:
		parse(path.read_text(encoding="utf-8"))
	except UrcError as error:
		print(f"{path}: FAIL")
		print(f"  {error.category} at line {error.line}: {error.message}")
		return 1

	print(f"{path}: OK")
	return 0


def command_convert(path: Path) -> int:
	"""Convert a source chart (.osu/.qua) to URC text on stdout."""
	try:
		text = path.read_text(encoding="utf-8")

		match path.suffix.lower():
			case ".osu":
				chart = convert_osu(parse_osu(text))
			case ".qua":
				chart = convert_qua(parse_qua(text))
			case suffix:
				print(f"error: {path}: unsupported file type: {suffix}", file=sys.stderr)
				return 1

		sys.stdout.write(write(chart))
	except UrcError as error:
		print(f"error: {path}: {error}", file=sys.stderr)
		return 1
	return 0


def main(argv: Sequence[str] | None = None) -> int:
	parser = argparse.ArgumentParser(prog="urc", description="Convert rhythm game charts to URC.")
	subparsers = parser.add_subparsers(dest="command", required=True)

	info_parser = subparsers.add_parser("info", help="print a summary of a URC chart")
	info_parser.add_argument("file", type=Path)
	info_parser.set_defaults(handler=command_info)

	validate_parser = subparsers.add_parser(
		"validate", help="check a URC chart against the spec rules"
	)
	validate_parser.add_argument("file", type=Path)
	validate_parser.set_defaults(handler=command_validate)

	convert_parser = subparsers.add_parser("convert", help="convert a source chart to URC.")
	convert_parser.add_argument("file", type=Path)
	convert_parser.set_defaults(handler=command_convert)

	args = parser.parse_args(argv)
	return args.handler(args.file)


if __name__ == "__main__":
	sys.exit(main())
