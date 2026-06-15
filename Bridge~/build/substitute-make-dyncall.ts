// Rolldown renderChunk plugin: rewrites
//   dynCall_<sig>(_EL_<var>, arg1, arg2, …)
// to the Emscripten macro form
//   {{{ makeDynCall('<sig>', '_EL_<var>') }}}(arg1, arg2, …)
//
// Runs in renderChunk (after Rolldown has finished bundling) so the
// {{{ }}} preprocessor syntax appears only in the final .jslib file.
// Emscripten expands it at Unity build time before treating the file as JS.
// At TypeScript compile time the plain dynCall_* form is used so the sources
// remain unit-testable in Node without Emscripten.
//
// The rewrite is AST-based (not regex) to correctly handle nested calls,
// multi-line argument lists, and comments inside the arg list.

import type { Plugin } from "rolldown";

const DYNCALL_RE = /^dynCall_([vif]+)$/;

type AstNode = {
  type: string;
  start: number;
  end: number;
  [key: string]: unknown;
};

type Replacement = { start: number; end: number; text: string };

function isAstNode(val: unknown): val is AstNode {
  return (
    val !== null &&
    typeof val === "object" &&
    typeof (val as Record<string, unknown>).type === "string" &&
    typeof (val as Record<string, unknown>).start === "number"
  );
}

function walkAst(node: AstNode, visit: (n: AstNode) => void): void {
  visit(node);
  for (const val of Object.values(node)) {
    if (Array.isArray(val)) {
      for (const item of val) {
        if (isAstNode(item)) walkAst(item, visit);
      }
    } else if (isAstNode(val)) {
      walkAst(val, visit);
    }
  }
}

export function transformDyncalls(code: string, ast: AstNode): string | null {
  const replacements: Replacement[] = [];

  walkAst(ast, (node) => {
    if (node.type !== "CallExpression") return;
    const callee = node.callee as AstNode | null | undefined;
    if (!callee || callee.type !== "Identifier") return;
    const calleeName = callee.name as string | undefined;
    if (!calleeName) return;
    const match = DYNCALL_RE.exec(calleeName);
    if (!match) return;
    const sig = match[1];

    const args = node.arguments as AstNode[];
    if (args.length === 0) return;
    const firstArg = args[0];
    if (firstArg.type !== "Identifier") return;
    const fnVarName = firstArg.name as string | undefined;
    if (!fnVarName || !fnVarName.startsWith("_EL_")) return;

    const restArgs = args.slice(1);
    const restStr =
      restArgs.length > 0
        ? code.slice(restArgs[0].start, restArgs[restArgs.length - 1].end)
        : "";

    replacements.push({
      start: node.start,
      end: node.end,
      text: `{{{ makeDynCall('${sig}', '${fnVarName}') }}}(${restStr})`,
    });
  });

  if (replacements.length === 0) return null;

  // Apply right-to-left so earlier positions are not affected by replacements.
  let result = code;
  for (const { start, end, text } of replacements.sort(
    (a, b) => b.start - a.start,
  )) {
    result = result.slice(0, start) + text + result.slice(end);
  }
  return result;
}

export function substituteMakeDyncall(): Plugin {
  return {
    name: "substitute-make-dyncall",
    renderChunk(code) {
      if (!code.includes("dynCall_")) return null;
      const ast = this.parse(code) as unknown as AstNode;
      return transformDyncalls(code, ast);
    },
  };
}
