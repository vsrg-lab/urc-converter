# Changelog

## [0.4.0] - 2026-11-25

### Changed

- **BMS / PMS (`.bms`, `.bme`, `.bml`, `.pms`)**:
  - Replaced dense LCM-based channel overlay with sparse rational-position merging (`O(sum(len))` time and memory) and changed `BmsChart.measures` (`Measures` in F#) to store raw per-channel line lists instead of a single pre-merged slot array.
  - Validated syntax on unused channels (BGM channel `01`, BGA, invisible notes, etc.) while skipping storage, avoided redundant line splitting during `#BASE` scanning, and stopped emitting zero slots on `#LNTYPE 1` long-note channels.
  - Replaced F# BMS note timing `Map` lookups with direct array indexing.
- **Quaver (`.qua`) & osu!mania (`.osu`)**:
  - Switched Python `.qua` parsing from full AST construction (`yaml.compose` with `SafeLoader`) to streaming event parsing (`yaml.parse` with `CSafeLoader`, falling back to `SafeLoader`), skipping unused sections in `O(1)` memory.
  - Stopped emitting redundant unchanged BPM and scroll velocity entries at every SV timestamp across all four languages.
  - Replaced `O(N^2)` BPM duration aggregation in Rust and F# `common_bpm` with `O(N)` map-indexed lookups and switched Rust timestamp deduplication from `BTreeSet` to sorted `Vec::dedup`.
- **StepMania (`.sm`, `.ssc`) & O2Jam (`.ojn`)**:
  - Replaced full-song measure anchor sweeps in StepMania with `O(1)` first-note measure boundary anchors, and skipped empty O2Jam measure gaps in `O(1)` arithmetic instead of measure-by-measure loops.
  - Optimized MSD tokenization across all four languages (boolean line-visibility tracking, contiguous run slicing, UTF-8 byte scanning in Rust, and `StringBuilder` in F#) and replaced F# StepMania timing `Map`s with arrays.
- **Shared timing & URC core parser**:
  - Used binary search for measure-anchor insertion in `build_timing`, avoided redundant re-sorting of already-ordered timestamps, and skipped copying/sorting in `check_hold_overlap` when notes are already sorted.
  - Streamed lines via `io.StringIO` and sorted notes in place in the Python URC parser.

### Fixed

- **BMS (`MemoryError` / OOM)**: Fixed out-of-memory crashes on real keysounded BMS charts where repeated coprime-length channel lines (especially channel `01` BGM) caused exponential LCM array blowup, and eliminated phantom `"00"` gaps on coprime `#LNTYPE 2` overlays.
- **StepMania & O2Jam (potential OOM)**: Prevented out-of-memory allocations and hangs on charts with extreme `#SCROLLS`/`#BPMS`/`#WARPS` beat positions or large O2Jam measure gaps.

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
