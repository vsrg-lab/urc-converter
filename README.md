# urc-converter

Converts vertical-scrolling rhythm game (VSRG) chart formats to
[URC (Universal Rhythm Chart)](https://github.com/vsrg-lab/urc-spec) 1.1 and
provides URC parsing, serialization, and playback rate scaling libraries in
multiple languages.

## URC libraries (`0.2.0`)

All four ports expose the same capabilities:

- **`parse`**: URC 1.x text → validated chart model (enforcing all 22 spec rules)
- **`write`**: chart model → canonical URC text
- **`scaleRate` / `scale_rate`**: playback rate scaling (`0.50x`–`3.00x` in `0.01` steps) with 1ms minimum same-lane gap/hold clamping and structured source-spacing warnings (`note-gap-clamped`, `hold-length-clamped`)
- **Source converters**: one-way conversion from supported game chart formats to URC
- **Structured errors**: machine-readable category — `syntax`, `unsupported-version`, `zero-length-hold`, `invalid-rate`, or `rule:N` (rules 1–22) — plus the 1-based source line (or byte offset for binary formats)

| Language | Package | Entry point |
| --- | --- | --- |
| Python | `urc-converter` | `urc_converter` |
| TypeScript | `urc-converter` | `urc-converter` (ESM) |
| F# (.NET) | `UrcConverter` | `UrcConverter.Urc` / `UrcConverter.Sources` |
| Rust | `urc-converter` | `urc_converter` |

## Repository layout

| Path | Contents |
| --- | --- |
| `shared/strings.json` | Canonical shared vocabulary (error categories, warnings, rules, sections, fields) |
| `libraries/python` | Python library (`uv`, hatchling) |
| `libraries/node` | TypeScript library (`pnpm`, ESM) |
| `libraries/dotnet` | F# library (`.NET 10`) |
| `libraries/rust` | Rust crate (`cargo`) |
| `apps/cli` | Python CLI (`urc`) |
| `apps/gui` | Windows WPF GUI (C#, WPF-UI, MVVM) |

## Supported source formats

- osu!mania `.osu`
- Quaver `.qua`
- BMS family: `.bms`, `.bme`, `.bml`, `.pms` (5+1, 7+1, 10+2, 14+2, PMS 9K/18K)
- StepMania `.sm`, `.ssc`
- O2Jam `.ojn` (plain and encrypted)

Conversion is one-way: source formats to URC.

### Roadmap

- Image-based chart reconstruction

## Apps

- **CLI**: `uv run --project apps/cli urc --help` (see [apps/cli/README.md](apps/cli/README.md))
- **WPF GUI**: `dotnet run --project apps/gui`
