using System.Runtime.CompilerServices;

// CordiSharpHost (in CordiSharp.Extensions.Hosting) reads the internally tracked
// plugin registrations from CordiSharpOptions.
[assembly: InternalsVisibleTo("CordiSharp.Extensions.Hosting")]
