using UnityEngine.Scripting;

// Force Unity to always link this assembly, even when nothing in the consumer
// project (or Assembly-CSharp) references it directly. Without this, IL2CPP
// skips the assembly entirely in consumer builds — link.xml preservations only
// take effect on assemblies the linker has decided to keep, not assemblies it
// decides to drop wholesale.
//
// Concretely: BridgedSessionLauncher's [RuntimeInitializeOnLoadMethod] hook
// would never fire and Conversation.SessionFactory stays null, so consumers see
// "No session factory is registered for the current platform" on the first
// StartSessionAsync call. Discovered while wiring the Getting Started demo.
//
// The in-tree ConversationSmokeTest sample doesn't hit this because its asmdef
// explicitly references ElevenLabs.Agents.WebGL, which keeps the assembly alive
// without needing AlwaysLinkAssembly.
[assembly: AlwaysLinkAssembly]
