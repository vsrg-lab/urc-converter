# urc-cli

Command-line interface for validating, inspecting, rate-scaling, and converting
VSRG chart files (`.osu`, `.qua`, `.bms`/`.bme`/`.bml`/`.pms`, `.sm`/`.ssc`,
`.ojn`, `.urc`) to URC 1.1.

## Usage

Run directly via `uv`:

```sh
uv run --project apps/cli urc --help
```

### Subcommands

```sh
# Print a summary of a .urc chart
urc info chart.urc

# Validate a .urc chart against the 22 specification rules
urc validate chart.urc

# Convert a source chart to canonical URC on stdout
urc convert chart.osu > chart.urc

# Convert with playback rate scaling (0.50..3.00 in 0.01 steps)
urc convert chart.osu --rate 1.25 > chart_1.25x.urc

# Scale an existing .urc chart
urc convert chart.urc --rate 1.50 > chart_1.50x.urc

# Convert BMS with #RANDOM branch options
urc convert song.bms --seed 42
urc convert song.bms --branches 2,1

# Select a single difficulty from multi-chart formats (.sm, .ssc, .ojn)
urc convert song.sm --chart 0
```

Any adjustment warnings (such as clamping sub-1ms same-lane gaps from the
source file) are printed to `stderr` so `stdout` always contains pure URC text.
