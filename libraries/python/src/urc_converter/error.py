"""Structured errors raised while parsing URC documents."""


class UrcError(Exception):
	"""Parse failure with a machine-readable category and source line."""

	def __init__(self, category: str, line: int, message: str) -> None:
		super().__init__(f"{category} at line {line}: {message}")
		self.category: str = category
		self.line: int = line
		self.message: str = message
