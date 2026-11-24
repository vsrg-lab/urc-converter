# urc-converter (Python)

URC 1.x parser, serializer, playback rate scaler, and VSRG chart converter
(`.osu`, `.qua`, `.bms`/`.bme`/`.bml`/`.pms`, `.sm`/`.ssc`, `.ojn`). See the
[repository](https://github.com/vsrg-lab/urc-converter) for the format overview
and the other ports (TypeScript, F#, Rust).

```sh
pip install urc-converter
```

## Parsing and writing URC

```python
from urc_converter import UrcError, parse, scale_rate, write

try:
	chart = parse(text)  # raises UrcError(category, line, message) on failure
	scaled = scale_rate(chart, 1.25)  # 0.50x..3.00x in 0.01 steps
	for warning in scaled.warnings:
		print(warning.code, warning.message)
	print(write(scaled.chart))  # canonical URC text
except UrcError as error:
	print(error.category, error.line, error.message)
```

## Converting source charts

```python
from pathlib import Path
from urc_converter import write
from urc_converter.sources import (
	convert_bms,
	convert_ojn,
	convert_osu,
	convert_qua,
	convert_sm,
	parse_bms,
	parse_ojn,
	parse_osu,
	parse_qua,
	parse_sm,
)

osu_chart = convert_osu(parse_osu(Path("chart.osu").read_text(encoding="utf-8")))
qua_chart = convert_qua(parse_qua(Path("chart.qua").read_text(encoding="utf-8")))
bms_chart = convert_bms(parse_bms(Path("chart.bms").read_bytes(), seed=42))
sm_charts = convert_sm(parse_sm(Path("chart.sm").read_text(encoding="utf-8")))
ojn_charts = convert_ojn(parse_ojn(Path("chart.ojn").read_bytes()))

print(write(osu_chart))
```
