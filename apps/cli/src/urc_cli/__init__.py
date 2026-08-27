"""Command-=line tool for converting rhythm game charts to URC."""

import argparse


def main() -> None:
	parser = argparse.ArgumentParser(
		prog="urc",
		description="Convert rhythm game charts to URC (Universal Rhythm Chart)"
	)
	_ = parser.parse_args()


if __name__ == "__main__":
	main()
