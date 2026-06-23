// Rolldown renderChunk plugin: lowers destructuring `const`/`let`/`var`
// declarations with a side-effect-free RHS to plain property/index reads.
//
// Why this exists: Emscripten's `tools/acorn-optimizer.js` JSDCE pass strips
// every destructuring declaration whose RHS evaluates to nothing that the
// pass recognises as a side effect. The pass's `VariableDeclarator` walker
// only reads `node.id.name` — which is `undefined` for `ObjectPattern` /
// `ArrayPattern` ids — so the destructured names are never recorded as
// `def`. The cleanup step then files the declaration under the literal
// string key `"undefined"`, marks it eliminateable, and drops it. The
// `${source}` and `${version}` template-literal references that *did* get
// recorded as `use` survive, producing a runtime `ReferenceError: source is
// not defined` from inside `WebSocketConnection.create` the first time a
// session opens. See Docs~/unity-issues/emscripten-jsdce-destructuring.md
// for the full root-cause writeup and the drop-when condition (Unity ≥6.5
// bundles Emscripten 4.0.19, which includes the upstream fix).
//
// We side-step the bug by rewriting the destructuring at bundle time:
//   const { name: source, version } = sourceInfo;
// becomes
//   const source = sourceInfo.name, version = sourceInfo.version;
// which JSDCE leaves alone because every declarator's `id.name` is a string.
//
// Scope of the rewrite — kept narrow so we never change runtime semantics:
//   * Only single-declarator `VariableDeclaration`s.
//   * Only `ObjectPattern` / `ArrayPattern` ids whose every entry is a plain
//     `Identifier` binding — no defaults, no rest, no nested patterns, no
//     computed keys.
//   * Only `Identifier` RHS, so repeated reads are safe (no getter
//     re-evaluation, no property-chain traversal duplicated).
//   * Anything that doesn't fit is left untouched — the original code path
//     continues to ship verbatim.
//
// The plan note about this fix being incomplete: Emscripten's JSDCE walker
// is also wrong for any other AST shape it parses but doesn't recurse into.
// If a future SDK bump introduces a new pattern that JSDCE strips (e.g.
// rest parameters in arrow functions, exotic class field initialisers), the
// fix belongs here too — see the "Open questions" section of the plan doc.

import type { Plugin } from "rolldown";

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

// A valid identifier in JavaScript — the conservative form we'll emit as
// dotted access. Anything outside this gets bracket-stringified.
const IDENT_RE = /^[A-Za-z_$][\w$]*$/;

function emitMemberAccess(initText: string, key: string): string {
  return IDENT_RE.test(key)
    ? `${initText}.${key}`
    : `${initText}[${JSON.stringify(key)}]`;
}

// Object-pattern declarator → list of `alias = init.key` assignments. Returns
// `null` if any property would need a transform we don't support (defaults,
// rest, computed keys, nested patterns).
function lowerObjectPattern(
  declarator: AstNode,
  initText: string,
): string | null {
  const id = declarator.id as AstNode;
  const properties = id.properties as AstNode[] | undefined;
  if (!properties) return null;

  const parts: string[] = [];
  for (const prop of properties) {
    if (prop.type !== "Property") return null; // RestElement, etc.
    if (prop.computed) return null;
    const valueNode = prop.value as AstNode;
    if (valueNode.type !== "Identifier") return null; // defaults, nested
    const keyNode = prop.key as AstNode;
    let keyName: string | null = null;
    if (keyNode.type === "Identifier") {
      keyName = keyNode.name as string;
    } else if (keyNode.type === "Literal") {
      const v = (keyNode as { value?: unknown }).value;
      if (typeof v === "string" || typeof v === "number") keyName = String(v);
    }
    if (keyName === null) return null;
    const alias = valueNode.name as string;
    parts.push(`${alias} = ${emitMemberAccess(initText, keyName)}`);
  }
  return parts.length === 0 ? null : parts.join(", ");
}

// Array-pattern declarator → list of `binding = init[i]` assignments.
// Holes (`const [, x] = arr;`) are tolerated; rest, defaults, and nested
// patterns are not.
function lowerArrayPattern(
  declarator: AstNode,
  initText: string,
): string | null {
  const id = declarator.id as AstNode;
  const elements = id.elements as (AstNode | null)[] | undefined;
  if (!elements) return null;

  const parts: string[] = [];
  for (let i = 0; i < elements.length; i++) {
    const el = elements[i];
    if (el === null) continue; // hole
    if (el.type !== "Identifier") return null; // RestElement, AssignmentPattern, nested
    parts.push(`${el.name as string} = ${initText}[${i}]`);
  }
  return parts.length === 0 ? null : parts.join(", ");
}

export function transformDestructuring(
  code: string,
  ast: AstNode,
): string | null {
  const replacements: Replacement[] = [];

  walkAst(ast, (node) => {
    if (node.type !== "VariableDeclaration") return;
    const declarations = node.declarations as AstNode[];
    if (declarations.length !== 1) return;
    const declarator = declarations[0];
    const id = declarator.id as AstNode;
    if (id.type !== "ObjectPattern" && id.type !== "ArrayPattern") return;
    const init = declarator.init as AstNode | null | undefined;
    // Only Identifier RHS — guarantees the repeated reads we emit are
    // semantically identical to the destructuring's single evaluation.
    if (!init || init.type !== "Identifier") return;
    const initText = code.slice(init.start, init.end);

    const lowered =
      id.type === "ObjectPattern"
        ? lowerObjectPattern(declarator, initText)
        : lowerArrayPattern(declarator, initText);
    if (lowered === null) return;

    const kind = node.kind as string; // "const" | "let" | "var"
    replacements.push({
      start: node.start,
      end: node.end,
      text: `${kind} ${lowered};`,
    });
  });

  if (replacements.length === 0) return null;

  // Right-to-left so earlier offsets remain valid as we splice.
  let result = code;
  for (const { start, end, text } of replacements.sort(
    (a, b) => b.start - a.start,
  )) {
    result = result.slice(0, start) + text + result.slice(end);
  }
  return result;
}

export function lowerDestructuring(): Plugin {
  return {
    name: "lower-destructuring",
    renderChunk(code) {
      const ast = this.parse(code) as unknown as AstNode;
      return transformDestructuring(code, ast);
    },
  };
}
