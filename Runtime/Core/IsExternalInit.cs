// Polyfill required for C# 9 init-only setters when targeting .NET Standard 2.1.
// IsExternalInit lives in System.Runtime.dll in .NET 5+, but .NET Standard 2.1 does
// not include it. Unity's Roslyn compiler requires the type to exist in any assembly
// that uses `init` properties; this internal copy satisfies that requirement without
// conflicting with other assemblies that carry their own copy.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
