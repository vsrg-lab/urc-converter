/**
 * Parser for BMS-family bytes.
 */
import type { BmsChart } from "./model.js";
import { UrcError } from "../../error.js";

/** Options for BMS parsing and random branch resolution. */
export interface BmsParseOptions {
	pms?: boolean;
	seed?: number | null;
	branches?: number[] | null;
}

class JavaRandom {
	private state: bigint;
	private static readonly MASK = (1n << 48n) - 1n;
	private static readonly MULT = 0x5DEECE66Dn;
	private static readonly ADD = 0xBn;

	constructor(seed: number) {
		this.state = (BigInt(seed) ^ JavaRandom.MULT) & JavaRandom.MASK;
	}

	private next(bits: number): bigint {
		this.state = (this.state * JavaRandom.MULT + JavaRandom.ADD) & JavaRandom.MASK;
		return this.state >> BigInt(48 - bits);
	}

	nextInt(bound: number): number {
		const b = BigInt(bound);
		if ((b & -b) === b)
			return Number((b * this.next(31)) >> 31n);

		while (true) {
			const bits = this.next(31);
			const val = bits % b;
			if (bits - val + (b - 1n) <= 0x7FFFFFFFn)
				return Number(val);
		}
	}
}

/**
 * Parses BMS-family bytes into a source model.
 */
export function parseBms(data: Uint8Array, options?: BmsParseOptions): BmsChart {
	const text = decode(data);
	const base = scanBase(text);
	const pms = options?.pms ?? false;
	const seed = options?.seed ?? null;
	const branches = options?.branches ?? null;

	const chart: BmsChart = {
		pms,
		base,
		title: null,
		artist: null,
		playLevel: null,
		bpm: null,
		lntype: 1,
		lnobj: null,
		bpmDefs: new Map(),
		stopDefs: new Map(),
		scrollDefs: new Map(),
		rates: new Map(),
		measures: new Map()
	};

	const randomGen = seed !== null ? new JavaRandom(seed) : null;
	const frames: Array<[boolean, boolean]> = [];
	let randomValue: number | null = null;
	let branchIndex = 0;

	const lines = splitLines(text);
	for (let i = 0; i < lines.length; i++) {
		const lineNo = i + 1;
		const line = lines[i].trim();
		if (!line.startsWith("#"))
			continue;
		const head = line.slice(1);

		if (head.length >= 6 && /^\d{3}/.test(head) && head[5] === ":") {
			if (!frames.some(frame => frame[0])) {
				const measure = Number.parseInt(head.slice(0, 3), 10);
				const channel = head.slice(3, 5);
				const payload = head.slice(6);
				addMessage(chart, measure, channel, payload, lineNo);
			}
			continue;
		}

		const spaceIndex = head.indexOf(" ");
		const command = spaceIndex === -1 ? head : head.slice(0, spaceIndex);
		const argument = spaceIndex === -1 ? "" : head.slice(spaceIndex + 1);
		const word = command.toUpperCase();

		if (word === "RANDOM") {
			const count = toInt(argument, lineNo, "#RANDOM");
			if (count < 1)
				throw new UrcError("syntax", lineNo, `#RANDOM count must be >= 1: ${count}`);
			let pick: number;
			if (branches !== null && branchIndex < branches.length) {
				pick = branches[branchIndex];
				if (pick < 1 || pick > count)
					throw new UrcError("syntax", lineNo, `branch pick out of range: ${pick}`);
			} else if (randomGen !== null)
				pick = randomGen.nextInt(count) + 1;
			else
				pick = 1;
			randomValue = pick;
			branchIndex++;
		} else if (word === "IF") {
			if (randomValue === null)
				throw new UrcError("syntax", lineNo, "unmatched #IF");
			const condition = toInt(argument, lineNo, "#IF");
			frames.push([randomValue !== condition, randomValue === condition]);
		} else if (word === "ELSEIF") {
			if (frames.length === 0)
				throw new UrcError("syntax", lineNo, "unmatched #ELSEIF");
			const condition = toInt(argument, lineNo, "#ELSEIF");
			const matched = frames[frames.length - 1][1] || randomValue === condition;
			frames[frames.length - 1] = [!matched, matched];
		} else if (word === "ELSE") {
			if (frames.length === 0)
				throw new UrcError("syntax", lineNo, "unmatched #ELSE");
			frames[frames.length - 1] = [frames[frames.length - 1][1], true];
		} else if (word === "ENDIF") {
			if (frames.length === 0)
				throw new UrcError("syntax", lineNo, "unmatched #ENDIF");
			frames.pop();
		} else if (
			word === "SETRANDOM" ||
			word === "ENDRANDOM" ||
			word === "SWITCH" ||
			word === "CASE" ||
			word === "SKIP" ||
			word === "DEF" ||
			word === "ENDSW" ||
			word === "SETSWITCH"
		)
			throw new UrcError("unsupported-version", lineNo, `unsupported BMS command: #${command}`);
		else if (frames.some(frame => frame[0]))
			continue;
		else if (word === "BPM")
			chart.bpm = toFloat(argument, lineNo, "#BPM");
		else if (
			(command.length === 5 && command.slice(0, 3).toUpperCase() === "BPM") ||
			(command.length === 8 && command.slice(0, 6).toUpperCase() === "EXBPM")
		)
			chart.bpmDefs.set(command.slice(-2), toFloat(argument, lineNo, command));
		else if (command.length === 6 && command.slice(0, 4).toUpperCase() === "STOP")
			chart.stopDefs.set(command.slice(-2), Math.abs(toFloat(argument, lineNo, command)) / 192.0);
		else if (command.length === 8 && command.slice(0, 6).toUpperCase() === "SCROLL")
			chart.scrollDefs.set(command.slice(-2), toFloat(argument, lineNo, command));
		else if (word === "LNTYPE") {
			chart.lntype = toInt(argument, lineNo, "#LNTYPE");
			if (chart.lntype !== 1 && chart.lntype !== 2)
				throw new UrcError("syntax", lineNo, `unsupported #LNTYPE: ${chart.lntype}`);
		} else if (word === "LNOBJ")
			chart.lnobj = argument.trim() || null;
		else if (word === "TITLE")
			chart.title = argument.trim() || null;
		else if (word === "ARTIST")
			chart.artist = argument.trim() || null;
		else if (word === "PLAYLEVEL")
			chart.playLevel = argument.trim() || null;
	}

	if (frames.length > 0)
		throw new UrcError("syntax", 1, "unterminated #IF block");

	return chart;
}

function decode(data: Uint8Array): string {
	let bytes = data;
	if (bytes.length >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf)
		bytes = bytes.subarray(3);

	try {
		const utf8 = new TextDecoder("utf-8", { fatal: true });
		return utf8.decode(bytes);
	} catch {
		try {
			const sjis = new TextDecoder("shift-jis", { fatal: true });
			return sjis.decode(bytes);
		} catch {
			throw new UrcError("syntax", 1, "undecodable bytes: expected UTF-8 or Shift_JIS");
		}
	}
}

function splitLines(text: string): string[] {
	let clean = text;
	if (clean.startsWith("\ufeff"))
		clean = clean.slice(1);
	return clean.replace(/\r\n/g, "\n").replace(/\r/g, "\n").split("\n");
}

function scanBase(text: string): number {
	for (const raw of splitLines(text)) {
		const parts = raw.trim().split(/\s+/);
		if (parts.length === 2 && parts[0].toUpperCase() === "#BASE") {
			const base = Number.parseInt(parts[1], 10);
			if (Number.isNaN(base))
				throw new UrcError("syntax", 1, `invalid #BASE: ${parts[1]}`);
			if (base !== 36 && base !== 62)
				throw new UrcError("syntax", 1, `unsupported #BASE: ${base}`);
			return base;
		}
	}
	return 36;
}

function isIdChar(char: string): boolean {
	return (char >= "0" && char <= "9") || (char >= "A" && char <= "Z") || (char >= "a" && char <= "z");
}

function toInt(value: string, lineNo: number, name: string): number {
	const trimmed = value.trim();
	if (!/^[+-]?\d+$/.test(trimmed))
		throw new UrcError("syntax", lineNo, `invalid ${name}: ${value}`);
	const num = Number.parseInt(trimmed, 10);
	if (Number.isNaN(num))
		throw new UrcError("syntax", lineNo, `invalid ${name}: ${value}`);
	return num;
}

function toFloat(value: string, lineNo: number, name: string): number {
	const trimmed = value.trim();
	const num = Number.parseFloat(trimmed);
	if (Number.isNaN(num))
		throw new UrcError("syntax", lineNo, `invalid ${name}: ${value}`);
	return num;
}

function addMessage(chart: BmsChart, measure: number, channel: string, payload: string, lineNo: number): void {
	if (channel === "02") {
		const rate = toFloat(payload, lineNo, "measure length");
		if (rate < 0)
			throw new UrcError("syntax", lineNo, `negative measure length: ${payload}`);
		chart.rates.set(measure, rate);
		return;
	}
	if (payload.length % 2 !== 0 || ![...payload].every(isIdChar))
		throw new UrcError("syntax", lineNo, `malformed object list: "${payload}"`);

	const ids: string[] = [];
	for (let i = 0; i < payload.length; i += 2)
		ids.push(payload.slice(i, i + 2));

	let measureMap = chart.measures.get(measure);
	if (!measureMap) {
		measureMap = new Map();
		chart.measures.set(measure, measureMap);
	}
	let channelList = measureMap.get(channel);
	if (!channelList) {
		channelList = [];
		measureMap.set(channel, channelList);
	}
	channelList.push(...ids);
}
