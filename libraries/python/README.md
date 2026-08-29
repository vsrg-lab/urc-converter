# urc-converter (Python)

URC 1.x parser and writer. See the [repo root](../../README.md) for the format
overview and the other ports (TypeScript, F#, Rust).

```sh
pip install urc-converter
```

```python
from urc_converter import UrcError, parse, write

try:
    chart = parse(text)      # raises UrcError(category, line, message) on failure
    print(write(chart))      # canonical URC text
except UrcError as error:
    print(error.category, error.line)
```