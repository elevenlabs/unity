using System.Runtime.CompilerServices;
using UnityEngine.Scripting;

// Edit-mode tests in ElevenLabs.Agents.Native.Tests exercise internal factories
// (URL construction, handshake) and the test-only seam that accepts a
// pre-paired WebSocket so the transport can be driven against an in-memory
// server fixture without touching real networking.
[assembly: InternalsVisibleTo("ElevenLabs.Agents.Native.Tests")]

// Force Unity to always link this assembly, even when nothing in the consumer
// project (or Assembly-CSharp) references it directly. Without this, IL2CPP
// skips the assembly entirely in consumer standalone / mobile builds and
// NativeSessionLauncher's [RuntimeInitializeOnLoadMethod] hook never fires —
// Conversation.SessionFactory stays null and consumers see
// "No session factory is registered for the current platform" on the first
// StartSessionAsync call. Same rationale as Runtime/WebGL/AssemblyInfo.cs.
[assembly: AlwaysLinkAssembly]
