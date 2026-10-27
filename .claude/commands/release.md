# Release

Create a new release of the ElevenLabs Unity SDK (`io.elevenlabs.agents`). There is no registry publish: consumers install through UPM's git URL, so the `vX.Y.Z` tag is the release, and the GitHub Release carries the notes from `CHANGELOG.md`.

## Arguments

The user may provide a version number (for example, `0.2.0`). If not provided:
1. Run `git describe --tags --abbrev=0` to get the latest tag (none before `v0.1.0`).
2. Read the `## [Unreleased]` section of `CHANGELOG.md` and skim `git log --oneline <latest-tag>..main` for anything user-facing it is missing.
3. Pick the next semantic version from the changes. While the SDK is `0.x`:
   - Patch for fixes.
   - Minor for new features and for breaking public API changes.
4. Confirm the version with the user before proceeding.

## Version locations

| File | Change |
|------|--------|
| `package.json` | `"version": "X.Y.Z"` |
| `CHANGELOG.md` | Move the `[Unreleased]` entries under a new `## [X.Y.Z] - YYYY-MM-DD` heading, leave an empty `## [Unreleased]` above it, and update the link references at the bottom |

The tag must be `v` plus `package.json`'s `version` exactly, since UPM resolves `https://github.com/elevenlabs/unity.git#vX.Y.Z` against it.

The link references follow Keep a Changelog:

```markdown
[Unreleased]: https://github.com/elevenlabs/unity/compare/vX.Y.Z...HEAD
[X.Y.Z]: https://github.com/elevenlabs/unity/compare/vPREVIOUS...vX.Y.Z
```

Leave the README's install URL unpinned and the bug report template's version placeholder alone.

## Steps

1. **Verify clean state**: Run `git status` on `main` and `git pull origin main`. Abort if there are uncommitted changes or if not on `main`.

2. **Check CI on `main`**: The release is cut from `main`'s head, so its `Lint`, `Unity Edit Mode tests` and `Integration tests` runs must be green:
   ```bash
   gh run list --branch main --limit 6 --json workflowName,conclusion,headSha
   ```
   Running Unity locally is optional; the commands are in `.claude/CLAUDE.md`.

3. **Update versions**: Edit `package.json` and `CHANGELOG.md` as described above. Date the entry with the day the tag will be pushed.

4. **Search for the old version**:
   ```bash
   git grep -n "OLD_VERSION" -- ':!*.meta' ':!Docs~/plans'
   ```
   Verify any remaining references are intentional.

5. **Open a release PR**: `main` requires status checks, so the bump goes through a PR:
   ```bash
   git switch -c release/vX.Y.Z
   git add package.json CHANGELOG.md
   git commit -m "chore: release X.Y.Z"
   git push -u origin release/vX.Y.Z
   gh pr create --title "Release X.Y.Z" --body "..."
   ```
   Wait for checks and a review, then squash-merge.

6. **Confirm with user**: Before tagging, show the user:
   - The version being released and the merge commit the tag will point at.
   - The release notes (the `CHANGELOG.md` section for `X.Y.Z`).
   - The exact commands that will push the tag and publish the release.

   A pushed tag is what consumers pin to, so do not move or delete it once published; ship a new patch instead.

7. **Tag the merge commit**:
   ```bash
   git switch main && git pull origin main
   git tag vX.Y.Z
   git push origin vX.Y.Z
   ```

8. **Create the GitHub Release** with the changelog section as notes:
   ```bash
   awk '/^## \[X\.Y\.Z\]/{f=1;next} /^## \[|^\[[^]]+\]: /{f=0} f' CHANGELOG.md > /tmp/notes.md
   gh release create vX.Y.Z --verify-tag --title "vX.Y.Z" --notes-file /tmp/notes.md
   ```

9. **Smoke-install**: In a clean Unity 6.3 project, install `https://github.com/elevenlabs/unity.git#vX.Y.Z` from the Package Manager and confirm it resolves and compiles.

10. **Report**: Share the GitHub Release URL and the pinned install URL:
    ```
    https://github.com/elevenlabs/unity.git#vX.Y.Z
    ```
