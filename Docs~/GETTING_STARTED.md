# Getting started

A 10-minute walkthrough: install the SDK, create an agent, and put a talking
cube in your scene that greets the player when they walk up to it.

There are two routes:

- **[Option A — start from the sample](#option-a--start-from-the-sample)**
  (recommended). Import the **GettingStarted** sample via UPM, drop in your
  agent ID, press Play. ~3 steps.
- **[Option B — add to your own project](#option-b--add-to-your-own-project)**.
  Same MonoBehaviour, but you wire it into an existing scene yourself. Use
  this when the sample's capsule + ground plane isn't where you want to start.

Both routes assume the [prereqs](#prereqs) below are in place.

## Prereqs

1. **Unity 6.3 LTS or later** (6000.3.0f1+). The SDK uses scriptable-audio
   APIs that landed in Unity 6.3 — earlier versions won't compile against it.
   See [`COMPATIBILITY.md`](../COMPATIBILITY.md) for the full requirement
   matrix.

2. **A free ElevenLabs account.** Sign up at
   [elevenlabs.io](https://elevenlabs.io); the free tier includes enough
   credit to run this walkthrough.

3. **An agent.** In the ElevenLabs dashboard, go to **Conversational AI →
   Agents → Create**. The defaults are fine for this walkthrough, with two
   tweaks:
   - **Security → Authentication: off.** This walkthrough uses the public
     WebSocket transport, which doesn't sign URLs. Agents with auth on need
     `SignedUrl` instead and aren't covered here.
   - **Security → Overrides → First message: on** (only needed if you want
     each box to use a different greeting — Option B step 5 walks through
     this). Same for **TTS → Voice ID** if you want per-box voices.

   Copy the agent ID — you'll need it in step 2 of either route.

---

## Option A — start from the sample

This is the path the README's landing demo follows.

1. **Install the SDK** via Window → Package Manager → **+** → **Install
   package from git URL…** and paste:

   ```text
   https://github.com/elevenlabs/unity.git
   ```

2. **Import the sample**: in the Package Manager, select **ElevenAgents** →
   **Samples** → **GettingStarted** → **Import**. The files land under
   `Assets/Samples/ElevenAgents/<version>/GettingStarted/`.

3. **Create the config asset**: right-click in the Project view → **Create
   → ElevenLabs → Samples → Talking Box Agent Config**. Move the asset into
   any folder named `Resources` under `Assets/` (e.g.
   `Assets/Resources/TalkingBoxAgentConfig.asset`). Paste your agent ID
   into the `Agent Id` field.

4. **Open** `Assets/Samples/.../GettingStarted/Scenes/GettingStarted.unity`
   and press **Play**. WASD walks, mouse looks. Walk into a cube — it
   greets you in character and bobs in time with its voice. Walk away — the
   session ends cleanly.

If nothing happens: the Console will tell you why. The most common cause is
the config asset not being under a `Resources/` folder (the sample loads it
via `Resources.Load<TalkingBoxAgentConfig>("TalkingBoxAgentConfig")` so the
folder name matters).

---

## Option B — add to your own project

Use this when you'd rather paste the components into an existing scene than
import the sample wholesale. End result is the same: a cube that greets the
player on trigger enter and ends the session on trigger exit.

### 1. Install the SDK

Window → Package Manager → **+** → **Install package from git URL…**:

```text
https://github.com/elevenlabs/unity.git
```

### 2. Configure Player Settings

The defaults work on standalone, mobile, and the Editor. For **WebGL** you
need two additional flips — see [`COMPATIBILITY.md`](../COMPATIBILITY.md#additional-webgl-setup)
for the rationale and failure modes if they're wrong:

<details>
<summary>WebGL-only Player Settings</summary>

- **Player Settings → Player → Other Settings → Api Compatibility Level** =
  **.NET Standard 2.1** (Unity 2023.1+ defaults to this).
- **Player Settings → WebGL → Publishing Settings → Use WebAssembly.Table**
  = **on**. The SDK ships a build preprocessor that fails the build with
  remediation steps if this is off, so you won't ship a broken build by
  accident.

</details>

### 3. Add `TalkingBoxAgentConfig.cs`

Create a new C# script at `Assets/Scripts/TalkingBoxAgentConfig.cs`:

```csharp
#nullable enable

using UnityEngine;

[CreateAssetMenu(
    menuName = "ElevenLabs/Talking Box Agent Config",
    fileName = "TalkingBoxAgentConfig"
)]
public sealed class TalkingBoxAgentConfig : ScriptableObject
{
    [SerializeField]
    private string agentId = "";

    public string AgentId => agentId;
}
```

Then **Create → ElevenLabs → Talking Box Agent Config** in the Project view,
move the asset under any `Resources/` folder
(`Assets/Resources/TalkingBoxAgentConfig.asset`), and paste your agent ID.

> **Gitignore the asset** if you're checking the project into a public repo.
> The sample's `.gitignore` does this — copy that pattern.

### 4. Add `TalkingBox.cs`

Create `Assets/Scripts/TalkingBox.cs`. The full file is in the sample at
[`Samples/GettingStarted/Scripts/TalkingBox.cs`](../Samples~/GettingStarted/Scripts/TalkingBox.cs);
copy it verbatim or use the snippets below as the basis for your own. The
four regions worth understanding:

**Lifecycle — open on enter, close on exit:**

```csharp
private void OnTriggerEnter(Collider other) => _ = StartTalkingAsync();
private void OnTriggerExit(Collider other) => _ = StopTalkingAsync();

private async Awaitable StartTalkingAsync()
{
    var options = new ConversationOptions
    {
        AgentId = config.AgentId,
        DynamicVariables = /* see below */,
        Overrides         = /* see below */,
        OutputAudioSource = audioSource,
    };
    activeConversation = await Conversation.StartSessionAsync(options);
}

private async Awaitable StopTalkingAsync()
{
    await activeConversation.EndSession();
    activeConversation = null;
}
```

`Conversation.StartSessionAsync` is the single entry point — it picks the
right transport per platform (WebGL bridge vs. native WebSocket) and hands
back a `Conversation` handle once the session is live.

**Dynamic variables — per-instance values the agent can interpolate:**

```csharp
DynamicVariables = new Dictionary<string, object>
{
    ["color"] = "yellow",
    ["mood"]  = "happy",
}
```

In the dashboard, the agent's system prompt and first message can reference
`{{color}}` / `{{mood}}` — each session substitutes the values you pass.
That's how the two cubes in the sample (yellow/happy and red/angry) share
one agent.

**Overrides — change the first message and voice per session:**

```csharp
Overrides = new ConversationConfigOverride
{
    Agent = new ConversationConfigOverrideAgent
    {
        FirstMessage = "Hi there! I'm a {{color}} cube, and I'm feeling {{mood}} today.",
    },
    Tts = new ConversationConfigOverrideTts { VoiceId = "your-voice-id" },
}
```

Both overrides require the matching dashboard toggles (**Security →
Overrides**). Omit the `Tts` field if you don't need a per-box voice.

**Output audio + volume-driven visual:**

```csharp
OutputAudioSource = audioSource, // assign in the inspector
...
private void Update()
{
    float rms = activeConversation?.GetOutputVolume() ?? 0f;
    // drive a bob, mouth-flap, shader param — whatever fits.
}
```

Pass an `AudioSource` to `OutputAudioSource` so the agent's voice
spatialises through it (3D position, rolloff, panning). Use
`Conversation.GetOutputVolume()` for envelope-driven visuals — it returns a
scalar in `[0, 1]` on **both native and WebGL**, unlike
`AudioSource.GetOutputData(...)` which returns silence on WebGL (see
[`COMPATIBILITY.md`](../COMPATIBILITY.md#dont-read-pcm-directly-off-the-supplied-audiosource)
for why).

### 5. Build the scene

In an empty scene:

1. **Add a cube** (GameObject → 3D Object → Cube). Add a **SphereCollider**
   to it with **Is Trigger = on**, radius ~2. *This is what fires
   `OnTriggerEnter` / `OnTriggerExit`* — the cube's own BoxCollider is the
   solid surface; the SphereCollider is the conversation zone around it.
2. **Add an AudioSource** to the cube. Leave the clip empty; the SDK writes
   into it at runtime. Bump **Spatial Blend** to 1.0 for 3D audio.
3. **Add the `TalkingBox` component** and drag the cube's AudioSource into
   the `Audio Source` slot.
4. **Add a player**: a Capsule with a **non-trigger** Collider (the default
   CapsuleCollider works) and a child Camera at head height. Drop the
   sample's [`SimplePlayerController`](../Samples~/GettingStarted/Scripts/SimplePlayerController.cs)
   on it for WASD + mouse-look, or use your own controller — anything that
   moves a collider into the trigger will work.
5. **Press Play.** Walk into the cube; the agent greets you. Walk away; the
   session ends.

---

## What to try next

- **A second character on the same agent.** Duplicate the cube, change the
  `color` and `mood` inspector fields, give it a different first-message
  override. Same agent ID, two distinct personalities. Only one box talks
  at a time — the static `currentlyActive` guard in `TalkingBox` ends the
  previous session before opening a new one.
- **Tune the bob.** `peakOffsetY` controls amplitude, `volumeSensitivity`
  controls how aggressively the RMS saturates, `smoothingTau` controls how
  snappy the response is. Smaller `smoothingTau` = twitchier.
- **Per-box voices.** Enable **TTS → Voice ID** overrides in the dashboard
  and paste a voice ID from your ElevenLabs library into `voiceIdOverride`.
- **Replace the bob with whatever fits your game** — a mouth-flap blendshape,
  a shader emission, a particle burst. All driven from the same
  `GetOutputVolume()` reading.

## Where to go from here

- [`COMPATIBILITY.md`](../COMPATIBILITY.md) — platform requirements,
  WebGL-specific behaviour, the cross-platform vs. native-only audio API
  table.
- [`ERROR_HANDLING.md`](./ERROR_HANDLING.md) — the exception surface plus
  the `Conversation.ErrorOccurred` event, with notes on which exceptions
  are catchable vs. programmer errors.
- [`ARCHITECTURE.md`](./ARCHITECTURE.md) — how the SDK is laid out
  internally (transports, audio pipeline, codegen) when you want to
  contribute or just understand the seams.
