# Coding-agent skills shipped with the Unity SDK

**Status:** Brainstorm, 2026-06-23
**Driver:** Two facts converging — (a) Anthropic's Agent Skills spec is now the de-facto format for shipping curated AI guidance alongside an SDK, and (b) the [agent-component.md](./agent-component.md) "AI assistant" persona only works if the AI knows our SDK's surface. Without skills, every coding-agent conversation rediscovers the SDK from source, repeatedly hits the same papercuts (`Awaitable`, `[SerializeReference]`, `#if UNITY_WEBGL` guards, the WebGL vs native audio split), and produces brittle code. Skills bridge that gap.
**Precedents:**
- [`elevenlabs/skills`](https://github.com/elevenlabs/skills) — central MIT-licensed product-tier skill repo (`agents`, `text-to-speech`, `music`, `setup-api-key`, …). Generic, cross-language (Python / JS / cURL).
- [`elevenlabs/packages/.agents/skills/elevenlabs:sdk-migration/`](https://github.com/elevenlabs/packages/blob/main/.agents/skills/elevenlabs%3Asdk-migration/SKILL.md) — repo-shipped, version-locked migration skill living inside the JS monorepo. **Direct precedent for "ship skills inside the SDK repo."**

---

## Why ship skills *from this repo*

Three viable homes for Unity-specific skills:

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| `elevenlabs/skills` (upstream) | Single discoverable index; one shared eval pipeline | Bloats the product-tier skill set with engine-specific concerns; couples the JS- and Python-centric repo to a Unity release schedule | ❌ Anti-pattern the user explicitly flagged |
| Nowhere (raw SDK only) | Zero new surface to maintain | Every conversation rediscovers the SDK; AI repeatedly trips on the same idioms; "AI assistant persona" from `agent-component.md` stays aspirational | ❌ Misses the upside |
| `elevenlabs-unity/.agents/skills/` (this repo) | Version-locked with the SDK; can reference in-repo types, plans, samples; mirrors the `elevenlabs/packages` precedent | New maintenance surface (each skill is also an eval suite); trigger wording is its own tuning workstream | ✅ Recommended |

The repo-shipped pattern keeps the skills in sync with the SDK they describe. When a v0.2 → v0.3 release renames a component method, the skill that mentions that method changes in the same PR. When the AsyncAPI spec re-vendors and a new event lands, the skill listing supported events grows alongside. Cross-repo coupling has the opposite property — the skill stays accurate only if a maintainer remembers to PR the other repo, which they will sometimes forget.

## The skill set

Five skills cover the surface a Unity developer reaches for, grouped by when they ship:

### Ship with v0.2 (alongside the agent component)

| Slug | Trigger phrasing | Body covers |
|---|---|---|
| `elevenlabs:unity-quickstart` | "Add ElevenLabs to my Unity project", "use ElevenLabs in Unity", "set up the ElevenLabs Unity SDK" | UPM install (Git URL or OpenUPM); `ELEVENLABS_API_KEY` for backend token-signing; minimal `Conversation.StartSessionAsync` example; pointer to either the component skill (drag-and-drop) or direct API (custom integrations) |
| `elevenlabs:unity-agent-component` | "Drag-and-drop an ElevenLabs agent on a GameObject", "add an ElevenLabs agent component", "make my NPC talk with ElevenLabs" | Full `ElevenLabsAgent` component story; `ElevenLabsAgentSetup.Configure` + `AgentSetupOptions`; credential provider picking; `AudioSource` wiring; `RunSelfTestAsync` after scaffold. Performs the setup through `unity command` against the live Editor |
| `elevenlabs:unity-client-tools` | "Register a client tool in Unity", "let the ElevenLabs agent call my C# code", "make the agent control my scene" | Typed `RegisterTool<TParams, TResult>` patterns (sync + async overloads); designer-tier `SimpleToolBinding` list on the component; `ClientToolException` error pattern; the reply-vs-fire-and-forget tradeoff |

### Ship with v0.3+ (when there's content to migrate or when audio routing widens)

| Slug | Trigger phrasing | Body covers |
|---|---|---|
| `elevenlabs:unity-migration` | "Upgrade ElevenLabs Unity SDK from X to Y", "migrate to the next ElevenLabs Unity version", "fix breaking changes after updating ElevenLabs Unity" | Per-version breaking changes; renamed APIs; deprecation paths; mirrors the [`elevenlabs:sdk-migration`](https://github.com/elevenlabs/packages/blob/main/.agents/skills/elevenlabs%3Asdk-migration/SKILL.md) format exactly. Useful only once we have a previous version to migrate from |
| `elevenlabs:unity-audio-routing` | "WebGL ElevenLabs audio doesn't play through my AudioSource", "spatial audio with ElevenLabs in Unity", "ElevenLabs agent audio routing" | Default-mode (JS `<audio>` element on WebGL) vs Unity-routed-mode tradeoffs; when to opt in; mixer + spatialisation behaviour by mode. Lands when the v0.3 Unity-routed-mode work makes the choice meaningful |

**Why split into five and not one giant `elevenlabs:unity`.** The trigger description is what the matcher actually reads — narrower triggers mean less context loaded per invocation, more accurate firing, and an evals story per-skill that's testable in isolation. The packages-repo precedent is per-topic (one skill per migration cycle), not per-product.

## Skill format and conventions

Adopting the format the precedents already use. One folder per skill at:

```
.agents/skills/elevenlabs:<name>/
  SKILL.md                  ← required
  references/               ← optional; long-form deep dives
    <topic>.md
  scripts/                  ← optional; runnable helpers (e.g., a C# scaffold script)
```

### Frontmatter

Matches `elevenlabs:sdk-migration`'s shape:

```yaml
---
name: elevenlabs:unity-agent-component
description: Drag-and-drop an ElevenLabs conversational agent onto a Unity GameObject. Use when adding voice AI to a Unity scene, configuring credential providers (public agent / signed URL / WebRTC token), wiring AudioSource for native playback, registering client tools, or scaffolding the setup via the unity CLI.
license: MIT
compatibility: Requires Unity 6 LTS or later, the elevenlabs-unity package (>=0.2.0), and either a manually-configured agent id or a backend that returns signed URLs / conversation tokens.
metadata: {"openclaw": {"requires": {"env": []}}}
---
```

Three deliberate choices to call out:

- **`name` is namespaced (`elevenlabs:unity-…`).** The colon-namespace lets the matcher disambiguate from other ElevenLabs skills (`elevenlabs:agents`, `elevenlabs:sdk-migration`) and other Unity-related skills shipped by unrelated projects.
- **`description` leads with the action verb the user will say.** "Drag-and-drop an ElevenLabs conversational agent onto a Unity GameObject" matches the phrasing real users type. "Use when…" extends the trigger surface to adjacent phrasings without restating the verb.
- **`compatibility` names a minimum SDK version.** Coding agents often arrive in repos already pinned to an older SDK; the skill needs to either work for that version or refuse cleanly. The minimum version is the cleanest contract.

### Body shape

The packages-repo skill body follows: short intro → numbered migration order → per-API section with Before/After code blocks. Translating that to the Unity skill set:

- **Quickstart skill:** install → credential setup → minimal example → pointer to the next skill ("for drag-and-drop, see `elevenlabs:unity-agent-component`").
- **Component skill:** when to use the component vs raw `Conversation` → `ElevenLabsAgentSetup.Configure` recipe as a `unity command` call → credential provider picking → `AudioSource` wiring → `RunSelfTestAsync` to verify. References point to `agent-component.md` for the design rationale.
- **Client-tools skill:** typed vs designer-tier registration → sync/async overloads → error handling → reply contracts → reference to the upstream agent-side tool definition docs.
- **Migration skill:** one section per version pair (v0.2→v0.3, v0.3→v0.4, …) with Before/After code blocks. The migration-order numbering matches the JS precedent exactly.
- **Audio-routing skill:** decision tree (WebGL? want spatial audio? want mixer routing? → default-mode vs Unity-routed-mode) → opt-in code → mode-specific gotchas.

### References folder

Each skill's `references/` is for content that would bloat the main body but the AI may need to load on demand. Patterns lifted from the `agents` skill in `elevenlabs/skills`:

- `references/installation.md` — full install matrix (Git URL, OpenUPM, local tarball, version compatibility table)
- `references/troubleshooting.md` — common error messages mapped to fixes
- `references/api-surface.md` — full C# API listing for the skill's topic, regenerated from XML doc comments

## Unity-specific content patterns

These are the patterns that distinguish Unity-skill content from the generic `elevenlabs/skills` body shape:

**Explicit Unity CLI callouts.** The generic skills don't know a live Editor can be driven from the terminal. The Unity skills lean in on the [`unity` CLI](https://docs.unity.com/en-us/unity-cli) (or the same commands as MCP tools via `unity mcp`):

> Add and configure the component in one call, using the Pipeline command the SDK registers (see [agent-component.md → design consequence 6](./agent-component.md#concrete-design-consequences)):
> ```bash
> unity command elevenlabs_agent_configure --target NPC --credentials PublicAgent \
>   --agent_id agent_xxx --add_audio_source true
> ```
> or, without the registered command, through `eval`:
> ```bash
> unity command eval 'var agent = ElevenLabs.Agents.ElevenLabsAgentSetup.Configure(
>   UnityEngine.GameObject.Find("NPC"),
>   new AgentSetupOptions(Credentials: CredentialKind.PublicAgent, AgentId: "agent_xxx", AddAudioSource: true));
> return agent.DescribeConfiguration();'
> ```
> Verify with `unity command console --level log` for the `[ElevenLabsAgent:NPC] configured` line.

The skill needs the project to have `com.unity.pipeline` (`unity pipeline install`). It can degrade gracefully when the CLI or package isn't available — same recipe, but the AI walks the user through the Inspector clicks instead.

**C# idioms over JS/Python.** `Awaitable<T>` not `Promise`, `MonoBehaviour` lifecycle not React hooks, `[SerializeReference]` not class-component-with-state. The skill's example code is C# only.

**UPM install path.** Add via Git URL into `Packages/manifest.json` (or OpenUPM scope registration), not `npm install`. Document both options; recommend Git URL for now since OpenUPM publishing is its own workstream.

**Editor-vs-Play mode awareness.** Unity-specific concept the generic `agents` skill never has to address. Each Unity skill mentions which steps work in Edit mode (configuration, validation, `RunSelfTestAsync`) vs need Play mode (active session, audio).

**Cross-references to in-repo plans.** Skills can link to `Docs~/plans/agent-component.md`, `Docs~/ARCHITECTURE.md`, etc. — since they ship in the same repo, the links don't rot when the SDK version moves. The generic-skills format has no equivalent (the references would point at a different repo).

## Distribution and version coupling

Two distinct audiences:

**SDK contributors** working in this repo. Claude Code auto-discovers `.agents/skills/` at the repo root; no setup. Matches `elevenlabs/packages`'s behaviour exactly. This is the same audience that already runs `dotnet csharpier check .` and `pnpm --dir Bridge~ run lint` — the skills are part of the workspace.

**SDK consumers** with the package installed via UPM into their Unity project. They want their coding agent to know about the SDK they're using, but their `Packages/manifest.json` entry doesn't drop files into the project root where Claude Code looks.

Three viable options for consumer distribution:

| Option | Pros | Cons |
|---|---|---|
| `claude plugin install elevenlabs-unity-skills` (or equivalent) | Standard Claude Code distribution; no Unity-side coupling | Users have to install the skill separately from the SDK; risks version drift between the installed skill and the installed SDK |
| UPM package includes the skills folder, user manually symlinks / copies to `~/.claude/skills/` | Skills ship inside the SDK package | Manual step; awkward path resolution; users won't do it |
| UPM Editor extension copies the skills into `~/.claude/skills/<repo>/` on import | Zero-touch for the user | Writes outside the project sandbox; security-sensitive; users may not want it |

**Recommendation:** option 1, decoupled. The skills get their own distribution channel; the SDK README points consumers at the install command. Version drift is handled by the same minimum-SDK-version field in the skill's `compatibility` line — old skill + new SDK fails closed with a clear message, rather than silently giving wrong advice.

This also means the skills can ship via `elevenlabs/skills` *as a separate set of namespaced entries* — `elevenlabs:unity-quickstart` etc. live there for discovery / install, while their source-of-truth content is curated in this repo and synced via a release script. That sidesteps the "bloat" concern by keeping the upstream repo as a thin distribution layer.

Open question: does that hybrid model (source-of-truth here, distribution there) match what `elevenlabs/skills` maintainers actually want?

## Evals

`elevenlabs/skills` ships an `evals/` directory at the repo root (trigger evals + functional evals). Mirroring that here means each skill carries:

- **Trigger evals** — prompts that should fire the skill ("how do I add an ElevenLabs agent to Unity") vs prompts that should not ("how do I add an animation to a Unity GameObject"). Confirms the `description` field is well-tuned.
- **Functional evals** — given the skill is loaded, does the AI produce working scaffolding code? For the component skill: does the produced `unity command` recipe actually compile and configure the component correctly when executed? This is the same shape as the existing `IntegrationTests~/` Playwright smokes, but for skill output.

Eval runner: tentatively reuse the `pnpm --dir IntegrationTests~/` infrastructure (Vitest already there; Playwright already there for running browser-side checks if a skill produces a WebGL flow). The Unity-side functional eval can execute the produced recipe headlessly against `TestProject/` with `unity run TestProject --command <name>` (or `eval_file` on a resident batch Editor), so no GUI Editor is needed. It's still a meaningful new test surface — probably its own follow-up plan once the first skill ships.

For v0.2, ship skills without evals and add the eval suite incrementally; for v1.0, every shipped skill has both trigger and functional evals before promotion.

## What this plan deliberately defers

- **Eval harness implementation.** Tracked above as a follow-up. The first skills can ship without evals; quality is human-verified for the first cycle.
- **OpenUPM publishing.** Skills can reference UPM Git URL installs in the meantime; OpenUPM is the v0.3+ polish item.
- **Multi-language coverage.** The generic `agents` skill ships Python / JS / cURL. Unity skills are C# only — this isn't really a tradeoff (the SDK *is* C# only), but it's worth saying so the format expectation doesn't carry over from the generic skills.
- **Voice-product skills** (TTS, sound effects, music) inside Unity. The `elevenlabs/skills` versions cover the API calls; bridging them to Unity (e.g., "generate a sound effect and import it as an AudioClip") is its own skill set, deferred to v0.4.
- **Skill bundling.** Whether `unity-quickstart` and `unity-agent-component` should be a single skill (faster trigger, more context per load) or stay separate (narrower triggers, smaller per-invocation context). Currently leaning separate; revisit after the first round of usage data.

## Open questions

1. **Hybrid distribution model.** Source-of-truth in this repo, namespaced entries in `elevenlabs/skills` as a distribution layer — viable, or does it conflict with what the upstream skills repo wants to be?
2. **`compatibility` enforcement.** Does the skill matcher actually read `compatibility` and refuse to load when the constraint fails, or is it advisory? If advisory, every skill needs a runtime "if SDK version < X, say so and stop" preamble in its body.
3. **`unity-quickstart` trigger overlap with the generic `agents` skill.** A user asking "how do I add ElevenLabs agents to Unity" matches both. The `agents` skill is more comprehensive on the API; the `unity-quickstart` skill is Unity-specific. Need to decide: does our skill's `description` explicitly say "use *instead of* `elevenlabs:agents` when the project is Unity," or does the matcher score both and pick whichever's better?
4. **References folder size budget.** The generic `agents` skill's references folder totals ~65 KB across 5 files. Unity skills will run larger because the C# API surface is bigger; an `api-surface.md` regenerated from XML doc comments could easily hit 100+ KB. Decide whether to inline the whole surface or stay selective.
5. **`unity-migration` first-release content.** Ships with v0.3 (when there's a v0.2 → v0.3 to migrate from). Decide now whether v0.1 → v0.2 also gets a migration entry (probably no — v0.1 is the first tagged release; nobody's expected to migrate from pre-tag).
6. **Skill author == component author?** The first round of skills is best written by the people who designed the underlying APIs (so the trigger description matches what the API was named for). Later rounds can be community-contributed. Decide whether to open contribution upfront or hold it to internal until the format is stable.

---

## Relationship to other plans

- [`agent-component.md`](./agent-component.md) — the `elevenlabs:unity-agent-component` skill is the primary delivery channel for that plan's "AI assistant" persona. The `ElevenLabsAgentSetup.Configure` / `RunSelfTestAsync` / `AgentConfigSnapshot` surface that plan proposes is the API the skill body is *built around*.
- [`v0.1-parity.md`](./v0.1-parity.md) — the `elevenlabs:unity-quickstart` skill depends on the SDK actually being usable in Editor + standalone (#9 native transport). Skills can't ship until v0.1 is tagged.
