# urc-converter

URC 1.x parser, serializer, playback rate scaler, and VSRG chart converter
(`.osu`, `.qua`, `.bms`/`.bme`/`.bml`/`.pms`, `.sm`/`.ssc`, `.ojn`) in Rust.
See the [repository](https://github.com/vsrg-lab/urc-converter) for the format
overview and the other ports (Python, TypeScript, F#).

```sh
cargo add urc-converter
```

## Parsing, rate scaling, and writing URC

```rust
use urc_converter::{parse, scale_rate, write};

let chart = parse(text)?;
let scaled = scale_rate(&chart, 1.25)?; // 0.50x..3.00x in 0.01 steps
for warning in &scaled.warnings {
    eprintln!("{}: {}", warning.code.as_str(), warning.message);
}
print!("{}", write(&scaled.chart)); // canonical URC text
```

## Converting source charts

```rust
use urc_converter::sources::{bms, ojn, osu, qua, sm};
use urc_converter::write;

let osu_chart = osu::convert_osu(&osu::parse_osu(&osu_text)?)?;
let qua_chart = qua::convert_qua(&qua::parse_qua(&qua_text)?)?;
let bms_chart = bms::convert_bms(&bms::parse_bms(&bms_bytes, &bms::BmsParseOptions::default())?)?;
let sm_charts = sm::convert_sm(&sm::parse_sm(&sm_text)?)?;
let ojn_charts = ojn::convert_ojn(&ojn::parse_ojn(&ojn_bytes)?)?;

print!("{}", write(&osu_chart));
```
