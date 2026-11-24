# Changelog

## [0.3.0] - 2026-11-24

### Changed

- **Quaver (`.qua`)**:
  - Added `bpm_does_not_affect_sv` (`bpmDoesNotAffectSv` / `BpmDoesNotAffectSv`) and `initial_scroll_velocity` (`initialScrollVelocity` / `InitialScrollVelocity`) fields to `QuaMap`.
  - Switched scroll velocity parsing from `ScrollSpeedFactors` to `SliderVelocities` and implemented `BPMDoesNotAffectScrollVelocity` compensation matching `Quaver.API`.
  - Supported `Keys4`/`Keys7` enum strings, arbitrary positive integer `Signature` values (plus `Quadruple`/`Triple` and `0` fallback), negative BPM handling via `abs(bpm)`, `ScrollGroupId` filtering, UTF-8 BOM handling, and raw scalar preservation for numeric metadata strings.
- **osu!mania (`.osu`)**:
  - Reset scroll velocity to `1.0` on uninherited timing points unless an inherited timing point exists at the same timestamp.
  - Defaulted omitted `Mode` to `0` across all ports, supported v5 6-field negative `beatLength` SVs, defaulted non-positive `meter` to `4`, rejected `NaN`/`Infinity`, zero `beatLength`, and positive inherited `beatLength`, and clamped high-OD judgment windows monotonically.
  - Normalized multiple BOMs, CR-only line endings, trimmed section headers, colon-less metadata lines, half-away-from-zero timestamp rounding, and pre-overlap note sorting.
- **BMS / PMS (`.bms`, `.bme`, `.bml`, `.pms`)**:
  - Merged duplicate channel definitions within the same measure via LCM overlay instead of concatenation.
  - Parsed channel `03` directly as 2-digit hexadecimal, required finite positive measure lengths (`> 0`) on channel `02` and positive extended BPMs on channel `08`, and normalized channel casing and base-36 object IDs while preserving base-62 case sensitivity.
  - Closed `#LNTYPE 2` holds at omitted measure boundaries and end of chart, ignored orphan `#LNOBJ` markers, accumulated multiple `#STOP` entries at the same position, and supported tab-separated headers.
- **StepMania (`.sm`, `.ssc`)**:
  - Preserved song-level timing inheritance when `.ssc` `#NOTEDATA` blocks contain empty timing tags (`#OFFSET:;`, `#BPMS:;`, etc.) or omit `#BPMS`, allowed empty song-level `#OFFSET:;` as `0.0`, and kept `#NOTEDATA` tags appearing after `#NOTES:` attached to the chart.
  - Added explicit warp-end resume points so time advances properly after a warp ends between events, ignored non-positive warps and negative `#DELAYS`, and skipped `{...}` attack brackets in note data.
  - Handled initial and never-ending negative/fast BPMs without emitting negative BPMs or overflowing 32-bit rows, unified MSD EOF tokenization without a trailing `;`, rejected non-finite numbers, and avoided mutating input `SmFile` notes during filtering.
- **O2Jam (`.ojn`)**:
  - Resolved section bounds from the smallest valid offset greater than the section start (falling back to file length), fixing files with `cover_offset == 0` or omitted middle difficulties.
  - Guarded `"new"` encryption detection when bytes `4..8` already contain `ojn\0`, read package event counts as unsigned 16-bit integers, rejected negative package counts and non-finite header BPMs, prevented overflow in `canonical_bpm` for large finite `f32` values, and reset meter to `4/4` on channel `0` fraction `1.0`.
  - Decoded `title`, `artist`, and `noter` per field (ASCII → UTF-8 → CP949) without failing on unused `ojm_file` bytes.
- **Shared timing**:
  - Accounted for time signature denominator (`note_value`) in `first_downbeat_after` and normalized `-0` in TypeScript `roundMs`.

### Fixed

- **BMS (`#ELSEIF`, `#EXBPMxx`, Rust UTF-8)**: Fixed inverted `#ELSEIF` evaluation after a matched `#IF`, `#EXBPMxx` command length check (7 characters), Rust non-ASCII byte-slicing panic, and F# reverse entry ordering.
- **StepMania (F#)**: Fixed `Preprocess` duplicating the first BPM when all BPMs start after beat `0` and switched internal row indexing to 64-bit integers.

## [0.2.2] - 2026-11-24

### Fixed

- Prevented scientific notation (e.g. `1e-05`) when formatting decimal numbers in the URC writer across Python, TypeScript, Rust, and F#.

## [0.2.1] - 2026-11-24

### Changed

- Updated CI/CD workflows (GitHub Actions OIDC publishing for PyPI, npm, crates.io, and NuGet; switched npm release to staged publishing).
- Simplified power-of-two check in Rust BMS `JavaRandom::next_int` (`is_power_of_two`) and removed hardcoded version header in `README.md`.

## [0.2.0] - 2026-11-24

### Added

- Playback rate scaling (`scale_rate` / `scaleRate`, `0.50x`–`3.00x` in `0.01` steps) across Python, TypeScript, Rust, F#, CLI, and WPF GUI, including 1ms same-lane gap/hold clamping and structured warnings (`note-gap-clamped`, `hold-length-clamped`).
- `zero-length-hold` and `invalid-rate` structured error categories in `shared/strings.json` and all four language libraries.
- CLI and WPF GUI updates supporting rate scaling and improved conversion workflows.

### Changed

- Unified zero-length hold handling across all source converters to report a `zero-length-hold` error instead of emitting degenerate holds.

## [0.1.0] - 2026-09-22

### Added

- Initial release of URC 1.1 parser (enforcing all 22 specification rules), canonical writer, and shared error/rule vocabulary (`shared/strings.json`) across Python, TypeScript (Node), Rust, and F# (.NET).
- One-way source chart converters for osu!mania (`.osu`), Quaver (`.qua`), BMS family (`.bms`, `.bme`, `.bml`, `.pms`), StepMania (`.sm`, `.ssc`), and O2Jam (`.ojn`, plain and encrypted).
- Python CLI (`urc-cli`) and Windows WPF GUI (`UrcConverter.Gui`).
