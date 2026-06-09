import js from "@eslint/js";
import globals from "globals";

export default [
  js.configs.recommended,
  {
    files: ["build/**/*.{ts,mjs,js}"],
    languageOptions: {
      globals: { ...globals.node },
    },
  },
];
