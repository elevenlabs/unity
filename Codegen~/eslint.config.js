import js from "@eslint/js";
import globals from "globals";

export default [
  js.configs.recommended,
  {
    files: ["src/**/*.{mjs,js}"],
    languageOptions: {
      globals: { ...globals.node },
    },
  },
];
