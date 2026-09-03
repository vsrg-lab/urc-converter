/**
 * Source model of a StepMania (.sm/.ssc) simfile.
 */

/** Timing segments in effect at the song level or for one chart. */
export interface Timing {
	offset: number;
	bpms: Array<[number, number]>;
	stops: Array<[number, number]>;
	delays: Array<[number, number]>;
	warps: Array<[number, number]>;
	scrolls: Array<[number, number]>;
	timesigs: Array<[number, number, number]>;
	fakes: Array<[number, number]>;
}

/** One note head; hold/roll pairs carry the tail row. */
export interface SmNote {
	row: number;
	track: number;
	kind: "tap" | "hold" | "roll" | "mine" | "lift" | "fake";
	tailRow: number | null;
}

/** One chart block (#NOTES in .sm, #NOTEDATA in .ssc). */
export interface SmChart {
	stepsType: string;
	description: string;
	difficulty: string;
	chartname: string;
	credit: string;
	timing: Timing | null;
	notes: SmNote[];
}

/** Parsed simfile: song metadata, song timing, and charts. */
export interface SmFile {
	title: string;
	subtitle: string;
	artist: string;
	credit: string;
	timing: Timing;
	charts: SmChart[];
}
