/**
 * Structured errors thrown while parsing URC documents.
 */

/**
 * Parse failure with a machine-readable category and source line.
 */
export class UrcError extends Error {
	readonly category: string;
	readonly line: number;

	constructor(category: string, line: number, message: string) {
		super(`${category} at line ${line}: ${message}`);
		this.name = "UrcError";
		this.category = category;
		this.line = line;
	}
}
