# urc-converter

URC 1.x parser and writer (ESM, type definitions included). See the
[repo root](../../README.md) for the format overview and the other ports
(Python, F#, Rust).

```sh
npm install urc-converter
```

```ts
import { parse, write, UrcError } from "urc-converter";

try {
	const chart = parse(text); // throws UrcError { category, line, message }
	console.log(write(chart)); // canonical URC text
} catch (error) {
	if (error instanceof UrcError) console.error(error.category, error.line);
}
```
