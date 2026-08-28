import js from "@eslint/js";
import stylistic from "@stylistic/eslint-plugin";
import { defineConfig, globalIgnores } from "eslint/config";
import { importX } from "eslint-plugin-import-x";
import jsdoc from "eslint-plugin-jsdoc";
import globals from "globals";
import tseslint from "typescript-eslint";

export default defineConfig([
	globalIgnores(["dist"]),
	{
		files: ["**/*.{ts,tsx}"],
		extends: [
			js.configs.recommended,
			tseslint.configs.recommended
		],
		languageOptions: {
			globals: globals.node
		},
		plugins: {
			"@stylistic": stylistic,
			"import-x": importX,
			jsdoc
		},
		rules: {
			"@stylistic/indent": ["error", "tab"],
			"@stylistic/quotes": ["error", "double"],
			"@stylistic/jsx-quotes": ["error", "prefer-double"],
			"@stylistic/semi": ["error", "always"],
			"@stylistic/comma-dangle": ["error", "never"],
			"@stylistic/brace-style": ["error", "1tbs", { allowSingleLine: true }],
			"curly": ["error", "multi"],
			"@stylistic/nonblock-statement-body-position": ["error", "below"],
			"@stylistic/arrow-parens": ["error", "as-needed"],
			"@stylistic/space-infix-ops": "error",
			"@stylistic/space-before-blocks": "error",
			"@stylistic/object-curly-spacing": ["error", "always"],
			"@stylistic/no-multiple-empty-lines": ["error", { max: 1 }],
			"eqeqeq": ["error", "always"],
			"import-x/order": ["error", {
				groups: ["builtin", "external", "internal", ["parent", "sibling", "index"]],
				"newlines-between": "always",
				alphabetize: { order: "asc", caseInsensitive: true }
			}],
			"jsdoc/require-jsdoc": ["warn", {
				publicOnly: true,
				contexts: ["TSTypeAliasDeclaration", "TSInterfaceDeclaration"],
				require: {
					FunctionDeclaration: true,
					ArrowFunctionExpression: true,
					ClassDeclaration: true
				}
			}]
		}
	}
]);
