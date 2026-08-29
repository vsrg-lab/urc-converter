# urc-converter

URC 1.x parser and writer. See the [repo root](../../README.md) for the format
overview and the other ports (Python, TypeScript, F#).

```sh
cargo add urc-converter
```

```rust
use urc_converter::{parse, write, UrcError};

let chart = parse(text)?;    // Err(UrcError { category, line, .. }) on failure
print!("{}", write(&chart)); // canonical URC text
```
