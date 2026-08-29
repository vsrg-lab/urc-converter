# urc-converter

Converts vertical-scrolling rhythm game (VSRG) chart formats to
[URC (Universal Rhythm Chart)](https://github.com/vsrg-lab/urc-spec) and provides
URC parsing libraries in multiple languages.

## URC parsing libraries

All four ports expose the same surface: `parse` (URC text → validated model,
enforcing the 22 spec validation rules), `write` (model → canonical URC
text), and structured errors carrying a machine-readable category — `syntax`,
`unsupported-version`, or `rule:N` (spec rules 1-22) — plus the 1-based
source line.

| Language   | Package         | Entry point           |
| ---------- | --------------- | --------------------- |
| Python     | `urc-converter` | `urc_converter`       |
| TypeScript | `urc-converter` | `urc-converter` (ESM) |
| F#         | `UrcConverter`  | `UrcConverter`        |
| Rust       | `urc-converter` | `urc_converter`       |

## Repository layout

| Path               | Contents                       |
| ------------------ | ------------------------------ |
| `libraries/python` | Python library (uv, hatchling) |
| `libraries/node`   | TypeScript library (pnpm)      |
| `libraries/dotnet` | F# library (FParsec)           |
| `libraries/rust`   | Rust crate                     |
| `apps/cli`         | Python CLI                     |
| `apps/gui`         | WPF GUI (planned)              |

## Supported source formats (planned)

- osu!mania `.osu`
- Quaver `.qua`
- BMS family: `.bms`, `.bme`, `.bml`, `.pms`
- StepMania `.sm`, `.ssc`
- DJMAX `.ojn`
- Image-based charts

Conversion is one-way: source formats to URC.
