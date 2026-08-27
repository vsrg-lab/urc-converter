# urc-converter

Converts vertical-scrolling rhythm game (VSRG) chart formats to
[URC (Universal Rhythm Chart)](https://github.com/vsrg-lab/urc-spec) and provides
URC parsing libraries in multiple languages.

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
