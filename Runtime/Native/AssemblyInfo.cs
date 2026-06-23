using System.Runtime.CompilerServices;

// Edit-mode tests in ElevenLabs.Agents.Native.Tests exercise internal factories
// (URL construction, handshake) and the test-only seam that accepts a
// pre-paired WebSocket so the transport can be driven against an in-memory
// server fixture without touching real networking.
[assembly: InternalsVisibleTo("ElevenLabs.Agents.Native.Tests")]
