using System.Diagnostics;
using System.Reflection;

namespace SearchEngine.Sharp.Tests;

// An allocation measured on unoptimized code says nothing about the library: in a Debug build every read of a
// constant table (static ReadOnlySpan<int> X => [...], as in UnicodeCasing.g.cs) allocates, and in an optimized
// build it does not. Tests that assert an exact allocation run only when the library is optimized.
public sealed class SkipUnlessOptimizedAttribute() : SkipAttribute(SkipReason)
{
    internal const string SkipReason = "Allocations are measured only on an optimized (Release) build of the library.";

    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(typeof(NameMatcher).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled == true);
}
