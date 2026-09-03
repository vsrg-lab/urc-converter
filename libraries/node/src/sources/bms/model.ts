/**
 * Source model of a BMS-family chart.
 */

/** Source model of a BMS-family chart. */
export interface BmsChart {
	pms: boolean;
	base: number;
	title: string | null;
	artist: string | null;
	playLevel: string | null;
	bpm: number | null;
	lntype: number;
	lnobj: string | null;
	bpmDefs: Map<string, number>;
	stopDefs: Map<string, number>;
	scrollDefs: Map<string, number>;
	rates: Map<number, number>;
	measures: Map<number, Map<string, string[]>>;
}
