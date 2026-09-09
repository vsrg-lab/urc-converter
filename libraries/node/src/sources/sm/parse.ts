/**
 * Parser for StepMania (.sm/.ssc) simfiles.
 */
import type { SmChart, SmFile, Timing } from "./model.js";
import {
	beatValue,
	expressions,
	pairs,
	parseFloatStrict,
	parseIntStrict,
	tokenize
} from "./msd.js";
import { parseNoteData } from "./notes.js";
import { UrcError } from "../../error.js";

const STEP_LANES: Record<string, number> = {
	"dance-single": 4,
	"dance-double": 8,
	"dance-solo": 6,
	"dance-threepanel": 3,
	"pump-single": 5,
	"pump-halfdouble": 6,
	"pump-double": 10,
	"kb7-single": 7,
	"techno-single4": 4,
	"techno-single5": 5,
	"techno-single8": 8,
	"techno-double4": 8,
	"techno-double5": 10,
	"techno-double8": 16,
	"maniax-single": 4,
	"maniax-double": 8,
	"pnm-five": 5,
	"pnm-nine": 9,
	"para-single": 5,
	"ds3ddx-single": 8,
	"ez2-single": 5,
	"ez2-double": 10,
	"ez2-real": 7,
	"kickbox-human": 4,
	"kickbox-quadarm": 4,
	"kickbox-insect": 6,
	"kickbox-arachnid": 8
};
const STEP_ALIASES: Record<string, string> = {
	"ez2-single-hard": "ez2-single",
	para: "para-single"
};

const TIMING_TAGS = new Set([
	"OFFSET",
	"BPMS",
	"STOPS",
	"FREEZES",
	"DELAYS",
	"WARPS",
	"SCROLLS",
	"FAKES",
	"TIMESIGNATURES"
]);

/**
 * Track count for a steps type; unsupported types are an error.
 */
export function resolveLanes(stepsType: string): number {
	const name = STEP_ALIASES[stepsType] ?? stepsType;
	const lanes = STEP_LANES[name];
	if (lanes === undefined)
		throw new UrcError(
			"unsupported-version",
			1,
			`unsupported steps type: ${stepsType || "(missing)"}`
		);
	return lanes;
}

/**
 * Parses a .sm or .ssc simfile into its source model.
 */
export function parseSm(text: string): SmFile {
	const simfile: SmFile = {
		title: "",
		subtitle: "",
		artist: "",
		credit: "",
		timing: emptyTiming(),
		charts: []
	};
	let chart: SmChart | null = null;

	for (const params of tokenize(text)) {
		const tag = params[0].toUpperCase();
		const value = params.length > 1 ? params[1] : "";

		if (tag === "NOTEDATA") {
			chart = emptyChart();
			continue;
		}
		if (tag === "NOTES" || tag === "NOTES2") {
			if (chart !== null) {
				chart.notes = parseNoteData(value, resolveLanes(chart.stepsType));
				simfile.charts.push(chart);
				chart = null;
			} else if (params.length >= 7) {
				const block = emptyChart();
				block.stepsType = params[1].trim();
				block.description = params[2].trim();
				block.difficulty = params[3].trim();
				block.credit = params[2].trim();
				block.notes = parseNoteData(params[6], resolveLanes(block.stepsType));
				simfile.charts.push(block);
			}
			continue;
		}

		if (chart === null)
			songTag(simfile, tag, value);
		else
			chartTag(simfile, chart, tag, value);
	}

	if (simfile.charts.length === 0)
		throw new UrcError("syntax", 1, "no chart in simfile");
	return simfile;
}

function emptyTiming(): Timing {
	return {
		offset: 0,
		bpms: [],
		stops: [],
		delays: [],
		warps: [],
		scrolls: [],
		timesigs: [],
		fakes: []
	};
}

function emptyChart(): SmChart {
	return {
		stepsType: "",
		description: "",
		difficulty: "",
		chartname: "",
		credit: "",
		timing: null,
		notes: []
	};
}

function songTag(simfile: SmFile, tag: string, value: string): void {
	switch (tag) {
		case "TITLE":
			simfile.title = value;
			break;
		case "SUBTITLE":
			simfile.subtitle = value;
			break;
		case "ARTIST":
			simfile.artist = value;
			break;
		case "CREDIT":
			simfile.credit = value;
			break;
		default:
			timingTag(simfile.timing, tag, value);
	}
}

function chartTag(simfile: SmFile, chart: SmChart, tag: string, value: string): void {
	switch (tag) {
		case "STEPSTYPE":
			chart.stepsType = value.trim();
			break;
		case "DESCRIPTION":
			chart.description = value.trim();
			break;
		case "DIFFICULTY":
			chart.difficulty = value.trim();
			break;
		case "CHARTNAME":
			chart.chartname = value.trim();
			break;
		case "CREDIT":
			chart.credit = value;
			break;
		default:
			if (TIMING_TAGS.has(tag)) {
				if (chart.timing === null)
					chart.timing = { ...emptyTiming(), offset: simfile.timing.offset };
				timingTag(chart.timing, tag, value);
			}
	}
}

function timingTag(timing: Timing, tag: string, value: string): void {
	switch (tag) {
		case "OFFSET":
			timing.offset = parseFloatStrict(value);
			break;
		case "BPMS":
			timing.bpms.push(...pairs(value, true));
			break;
		case "STOPS":
		case "FREEZES":
			timing.stops.push(...pairs(value, true));
			break;
		case "DELAYS":
			timing.delays.push(...pairs(value, true));
			break;
		case "WARPS":
			timing.warps.push(...pairs(value));
			break;
		case "SCROLLS":
			timing.scrolls.push(...pairs(value));
			break;
		case "FAKES":
			for (const entry of pairs(value))
				if (entry[1] > 0)
					timing.fakes.push(entry);
			break;
		case "TIMESIGNATURES":
			for (const parts of expressions(value, 3)) {
				const beat = beatValue(parts[0]);
				const numerator = parseIntStrict(parts[1]);
				const denominator = parseIntStrict(parts[2]);
				if (numerator >= 1 && denominator >= 1 && beat >= 0)
					timing.timesigs.push([beat, numerator, denominator]);
			}
	}
}

