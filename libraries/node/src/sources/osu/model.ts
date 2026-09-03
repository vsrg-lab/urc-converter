/**
 * Source model of an osu!mania beatmap.
 */

/** One `[TimingPoints]` entry, reduced to the fields we map. */
export interface OsuTimingPoint {
	time: number;
	beatLength: number;
	meter: number;
	uninherited: boolean;
}

/** One `[HitObjects]` entry, reduced to the fields we map. */
export interface OsuHitObject {
	x: number;
	time: number;
	isHold: boolean;
	endTime: number | null;
}

/** Source model of an osu!mania beatmap. */
export interface OsuBeatmap {
	mode: number;
	title: string | null;
	titleUnicode: string | null;
	artist: string | null;
	artistUnicode: string | null;
	creator: string | null;
	version: string | null;
	circleSize: number | null;
	overallDifficulty: number | null;
	timingPoints: OsuTimingPoint[];
	hitObjects: OsuHitObject[];
}
