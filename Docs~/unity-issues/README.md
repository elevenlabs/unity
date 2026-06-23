# Unity issues — upstream bug & limitation reports

Self-contained writeups of Unity bugs, limitations, or unexpected
behaviours we've worked around in this SDK. Each file is structured so
it can be handed to Unity support / a Unity engineering contact
without further editing — version pinning, minimal repro, expected vs
actual, the workaround applied, and the location of the workaround in
our tree.

A new file lands here whenever we discover a workaround for a Unity-side
issue worth propagating upstream. The bar is "if we had a Unity contact
in the room, we'd want them to see this." Internal bug reproducers that
turn out to be our own bugs do **not** land here — those belong in code
comments or implementation plans.

See the "Reporting Unity issues" section of
[`.claude/CLAUDE.md`](../../.claude/CLAUDE.md) for the workflow.

## Index

- [`link-xml-autodiscovery-file-package.md`](./link-xml-autodiscovery-file-package.md)
  — UnityLinker silently drops package-internal `link.xml` files when
  the package is referenced as a local file-path package whose root is
  the embedded host project's parent.
