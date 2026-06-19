using System.Runtime.CompilerServices;

// Edit-mode tests in ElevenLabs.Agents.WebGL.Tests exercise the internal
// registry types and other internal helpers in this assembly.
[assembly: InternalsVisibleTo("ElevenLabs.Agents.WebGL.Tests")]
// The conversation smoke sample reads internal handle-count properties on
// PromiseRegistry / CallbackRegistry to assert no JS handles leak after
// EndSession. Sample-only access; no new public surface.
[assembly: InternalsVisibleTo("ElevenLabs.WebGL.Samples.ConversationSmokeTest")]
