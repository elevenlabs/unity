# Emscripten `acorn-optimizer.js` JSDCE drops destructuring declarations from WebGL builds

**Unity version:** 6000.3.6f1 (Unity 6 LTS). Reproduces on every Unity
6.0-6.4 release; fixed for free in 6.5+.
**Bundled Emscripten:** 3.1.39-git (Unity 6.3) — buggy version. Fix
landed upstream in Emscripten 3.1.47 (Oct 2023).
**Platform reproduced:** WebGL builds (any browser; the bug is in the
build-time JS optimiser, not the runtime).
**Severity:** Silent at build time, fatal at runtime — the bundle
emits `${source}` references whose `const { name: source } = ...`
declaration has been deleted, producing a `ReferenceError: source is
not defined` on the first code path that touches the affected
function.
**Discovered:** 2026-06-22 while wiring the ElevenLabs SDK's
`WebSocketConnection.create` and `WebRTCConnection.create` through a
WebGL consumer build.

## Summary

Emscripten's `tools/acorn-optimizer.js` JSDCE (dead-code elimination)
pass walks `VariableDeclarator` nodes assuming `node.id` is always a
plain `Identifier`. For destructuring declarations (`const { x } =
obj`, `const [a, b] = arr`, `const { a: x, b: y } = obj`), `node.id`
is an `ObjectPattern` / `ArrayPattern` with no `.name`. The walker
records the declaration under the literal string `"undefined"`,
never visits the bound identifiers, and in the cleanup pass marks
the declaration eliminateable. The declaration is deleted; the
references to the destructured names elsewhere in the function body
are not, because they DID get recorded as `use`. Runtime hits a
`ReferenceError` on the first call into the affected function.

The bug is in Emscripten, not Unity directly — but Unity 6.0 through
6.4 ship a pre-fix Emscripten and inherit it.

## Minimal reproduction

Author this TypeScript:

```ts
function makeUrl(sourceInfo: { name: string; version: string }, base: string) {
  const { name: source, version } = sourceInfo;
  return `${base}?source=${source}&version=${version}`;
}
```

Run it through any bundler that lowers to ES2015 module output, then
through Unity's WebGL build with default optimisation. The shipped
`Web.framework.js` contains the `${source}` / `${version}`
template-literal references but no declaration that binds them. First
call to `makeUrl` throws:

```
ReferenceError: source is not defined
```

Three shorthand forms reproduce; one workaround form survives:

| Source                                 | After JSDCE          |
|----------------------------------------|----------------------|
| `const { x, y } = obj;`                | **deleted**          |
| `const { a: x, b: y } = obj;`          | **deleted**          |
| `const [x, y] = arr;`                  | **deleted**          |
| `const x = obj.a;` (plain id)          | kept                 |
| `const { x } = sideEffect();` (visible side effect on RHS) | kept |

Tested against Unity's bundled `tools/acorn-optimizer.js` directly
(Emscripten 3.1.39-git, acorn 8.7.1, terser 5.16.6).

## Expected behaviour

JSDCE should either:

- Recurse into `ObjectPattern` / `ArrayPattern` / `AssignmentPattern`
  inside `VariableDeclarator.id` and record each bound identifier as a
  `def`, OR
- Conservatively keep any declaration whose `id` is not a plain
  `Identifier`.

Either path is correct; the upstream fix went with option (1).

## Buggy code

[`tools/acorn-optimizer.js` `runJSDCE`'s `VariableDeclarator` walker](https://github.com/emscripten-core/emscripten/blob/3.1.39/tools/acorn-optimizer.js#L392):

```js
VariableDeclarator(node, c) {
  const name = node.id.name;                     // undefined for ObjectPattern/ArrayPattern
  ensureData(scopes[scopes.length - 1], name).def = 1;
  if (node.init) c(node.init);
},
```

## Upstream status

Already reported and fixed in Emscripten:

- **Issue:** [emscripten-core/emscripten#20393](https://github.com/emscripten-core/emscripten/issues/20393)
  — "Invalid Acorn optimization", filed Sept 2023 by @hoodmane
  (Pyodide). Identical root cause.
- **Fix:** [PR #20402](https://github.com/emscripten-core/emscripten/pull/20402)
  — "JSDCE: Add support for ObjectPattern declarations", merged
  Oct 5 2023 as commit `db199c4`. Replaced the buggy `node.id.name`
  read with a `walkPattern(node.id, c, callback)` helper that handles
  `ObjectPattern`, `ArrayPattern`, and `AssignmentPattern`.
- **First Emscripten release with the fix:** **3.1.47** (Oct 9 2023).

## Unity version matrix

| Unity                     | Emscripten           | Affected? |
|---------------------------|----------------------|-----------|
| 6.0 (6000.0)              | 3.1.38               | **yes**   |
| 6.3 (6000.3) — current    | 3.1.39-git           | **yes**   |
| 6.4 (6000.4)              | (undocumented 3.1.x) | likely    |
| 6.5 (6000.5)              | **4.0.19-unity**     | no        |
| 6.6 (6000.6) alpha+       | 4.0.19+              | no        |

Unity 6.5's Emscripten bump is documented in the
[6.5 release notes](https://docs.unity3d.com/6000.5/Documentation/Manual/WhatsNewUnity65.html)
("*Unity 6.5 and later uses the Emscripten compiler version
`4.0.19-unity` to compile Unity Web Player builds*"). Consumers on
6.5+ get the fix for free; everyone on 6.0–6.4 LTS is stuck.

## Workaround applied in this SDK

Rolldown plugin
[`Bridge~/build/lower-destructuring.ts`](../../Bridge~/build/lower-destructuring.ts),
wired into the `.jslib` bundler's postset mode only. After Rolldown
finishes, it walks the bundle AST and rewrites every `const`/`let`/`var`
whose declarator is an `ObjectPattern` / `ArrayPattern` with an
`Identifier` RHS and only-`Identifier` bindings into plain
`const x = obj.x, y = obj.y;` form. Anything fancier (defaults, rest
parameters, computed keys, nested patterns, non-`Identifier` RHS,
multi-declarator) is left alone — those shapes don't appear in the
SDK or our own sources today; if they ever do, the static-shape
regression test in
[`IntegrationTests~/src/conversation-shape.test.ts`](../../IntegrationTests~/src/conversation-shape.test.ts)
catches the regression as "this exact symbol disappeared from
`Web.framework.js`" rather than a runtime `ReferenceError`.

No new dependencies — acorn is already in Rolldown's tree. The plugin
mirrors the existing
[`Bridge~/build/substitute-make-dyncall.ts`](../../Bridge~/build/substitute-make-dyncall.ts)
plugin shape.

### Why not lower Rolldown's `transform.target`

Rolldown's target floor is `es2015`, which is the same level at which
destructuring became native syntax. There's no Rolldown-internal way
to lower it out. Adding `oxc-transform` or `@babel/*` for one
transform was disproportionate.

### Drop-when condition

When the SDK's minimum supported Unity moves to ≥6.5 (Emscripten
≥4.0.19, comfortably past the 3.1.47 fix),
`Bridge~/build/lower-destructuring.ts` and its test can be deleted,
the postset-mode plugin list in `Bridge~/build/bundle-jslib.ts` drops
back to `[substituteMakeDyncall()]`, the
`Plugins/WebGL/ElevenLabsConnection.jslib` regenerates without the
rewrites, and `conversation-shape.test.ts` accepts the original
destructuring form (the test already does — `hasDestructuring ||
hasLowered`).

## Asks for Unity

1. **Backport Emscripten PR #20402 into a `6000.3.x` LTS patch.** The
   ask is unusually small because (a) Unity already validated a much
   larger Emscripten jump for 6.5, so cherry-picking PR #20402 alone
   is a tiny delta against the 3.1.39 baseline, and (b) it's a strict
   bug-fix patch — no behavioural surface change for existing
   consumers. Same ask would help every 6.0 / 6.4 LTS user who
   bundles modern TypeScript into a `.jslib`.
2. **Document the Unity ↔ Emscripten version pairing publicly.** The
   matrix above was reconstructed from release notes and an `--version`
   check on the bundled toolchain. A first-party reference would have
   collapsed the upstream-version-pinning step from hours to minutes.

## Standalone gist for forwarding

Same writeup, polished for a Unity-contact email and stripped of
this-repo specifics so it can be forwarded freely:
<https://gist.github.com/kraenhansen/917d822cbbda818d27095ea80a470bf7>.
