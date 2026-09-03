/**
 * Source model of a .qua chart.
 */

/** One TimingPoints entry. */
export interface QuaTimingPoint {
	startTime: number;
	bpm: number;
	signature: number;
}

/** One ScrollSpeedFactors entry. */
export interface QuaSvPoint {
	startTime: number;
	multiplier: number;
}

/** One HitObjects entry. */
export interface QuaHitObject {
	startTime: number;
	lane: number;
	endTime: number;
	mine: boolean;
}

/** Source model of a .qua chart. */
export interface QuaMap {
	mode: number;
	hasScratchKey: boolean;
	title: string | null;
	artist: string | null;
	creator: string | null;
	difficultyName: string | null;
	timingPoints: QuaTimingPoint[];
	svPoints: QuaSvPoint[];
	hitObjects: QuaHitObject[];
}
