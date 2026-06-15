import js from "@eslint/js";
import globals from "globals";

export default [
  js.configs.recommended,
  {
    files: ["build/**/*.{mjs,js}"],
    languageOptions: {
      globals: { ...globals.node },
    },
  },
];
