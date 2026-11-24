# urc-converter

URC 1.x parser, serializer, playback rate scaler, and VSRG chart converter
(`.osu`, `.qua`, `.bms`/`.bme`/`.bml`/`.pms`, `.sm`/`.ssc`, `.ojn`) in
TypeScript (ESM, type definitions included). See the
[repository](https://github.com/vsrg-lab/urc-converter) for the format overview
and the other ports (Python, F#, Rust).

```sh
npm install urc-converter
```

## Parsing, rate scaling, and writing URC

```ts
import { parse, scaleRate, write, UrcError } from "urc-converter";

try {
	const chart = parse(text); // throws UrcError { category, line, message }
	const { chart: scaled, warnings } = scaleRate(chart, 1.25); // 0.50x..3.00x in 0.01 steps
	for (const warning of warnings)
		console.warn(warning.code, warning.message);
	console.log(write(scaled)); // canonical URC text
} catch (error) {
	if (error instanceof UrcError)
		console.error(error.category, error.line, error.message);
}
```

## Converting source charts

```ts
import { readFileSync } from "node:fs";
import { bms, ojn, osu, qua, sm, write } from "urc-converter";

const osuChart = osu.convertOsu(osu.parseOsu(readFileSync("chart.osu", "utf8")));
const quaChart = qua.convertQua(qua.parseQua(readFileSync("chart.qua", "utf8")));
const bmsChart = bms.convertBms(bms.parseBms(readFileSync("chart.bms"), { seed: 42 }));
const smCharts = sm.convertSm(sm.parseSm(readFileSync("chart.sm", "utf8")));
const ojnCharts = ojn.convertOjn(ojn.parseOjn(readFileSync("chart.ojn")));

console.log(write(osuChart));
```
