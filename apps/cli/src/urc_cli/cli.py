"""Subcommands of the urc CLI."""

import argparse
import sys
from collections.abc import Sequence
from pathlib import Path

from urc_converter import NoteType, UrcError, parse, write
from urc_converter.sources import (
	convert_bms,
	convert_osu,
	convert_qua,
	convert_sm,
	parse_bms,
	parse_osu,
	parse_qua,
	parse_sm,
)


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


def command_convert(
	path: Path,
	seed: int | None,
	branches: list[int] | None,
	chart: int | None,
) -> int:
	"""Convert a source chart to URC on stdout."""
	suffix = path.suffix.lower()

	try:
		if (seed is not None or branches is not None) and suffix not in (
			".bms",
			".bme",
			".bml",
			".pms",
		):
			print("error: --seed/--branches only apply to BMS sources", file=sys.stderr)
			return 1
		if chart is not None and suffix not in (".sm", ".ssc"):
			print("error: --chart only applies to SM/SSC sources", file=sys.stderr)
			return 1

		charts = None
		match suffix:
			case ".osu":
				charts = [convert_osu(parse_osu(path.read_text(encoding="utf-8")))]
			case ".qua":
				charts = [convert_qua(parse_qua(path.read_text(encoding="utf-8")))]
			case ".bms" | ".bme" | ".bml" | ".pms":
				charts = [
					convert_bms(
						parse_bms(
							path.read_bytes(),
							pms=suffix == ".pms",
							seed=seed,
							branches=branches,
						)
					)
				]
			case ".sm" | ".ssc":
				charts = convert_sm(parse_sm(path.read_text(encoding="utf-8")))
				if chart is not None:
					if not 0 <= chart < len(charts):
						print(
							f"error: {path}: chart index {chart} out of range"
							f" ({len(charts)} charts)",
							file=sys.stderr,
						)
						return 1
					charts = [charts[chart]]
			case unsupported:
				print(f"error: {path}: unsupported file type: {unsupported}", file=sys.stderr)
				return 1

		for index, converted in enumerate(charts):
			if index:
				sys.stdout.buffer.write(b"\n")
			sys.stdout.buffer.write(write(converted).encode("utf-8"))
	except UrcError as error:
		print(f"error: {path}: {error}", file=sys.stderr)
		return 1
	return 0


def _branch_list(value: str) -> list[int]:
	try:
		return [int(part) for part in value.split(",") if part]
	except ValueError:
		raise argparse.ArgumentTypeError(f"invalid branches: {value}") from None


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
	convert_parser.add_argument("--seed", type=int, help="seed for BMS #RANDOM expansion")
	convert_parser.add_argument(
		"--branches", type=_branch_list, help="explicit #RANDOM picks, e.g. 2,1"
	)
	convert_parser.add_argument(
		"--chart",
		type=int,
		help="chart index for SM/SSC simfiles (default: all charts)",
	)
	convert_parser.set_defaults(handler=command_convert)

	args = parser.parse_args(argv)
	if args.command == "convert":
		return command_convert(args.file, args.seed, args.branches, args.chart)

	return args.handler(args.file)


if __name__ == "__main__":
	sys.exit(main())
