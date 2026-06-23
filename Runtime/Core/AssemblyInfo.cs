using System.Runtime.CompilerServices;

// Platform-specific implementations of IConnection / IInputController /
// IOutputController live in sibling assemblies that need to see the
// internal contracts.
[assembly: InternalsVisibleTo("ElevenLabs.Agents.WebGL")]
[assembly: InternalsVisibleTo("ElevenLabs.Agents.WebGL.Tests")]
[assembly: InternalsVisibleTo("ElevenLabs.Agents.Native")]
[assembly: InternalsVisibleTo("ElevenLabs.Agents.Native.Tests")]
