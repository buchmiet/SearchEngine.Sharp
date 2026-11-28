using BenchmarkDotNet.Running;

namespace SearchEngine.Sharp.MicroBenchmarks;

// A named entry point instead of top-level statements: the referenced Benchmarks project has top-level
// statements too, and both would define a type called Program that this project sees through InternalsVisibleTo.
internal static class MicroBenchmarkProgram
{
    private const string FingerprintFlag = "--fingerprint";

    internal static void Main(string[] args)
    {
        if (args.Contains(FingerprintFlag))
        {
            WriteFingerprint(args);
            return;
        }

        Console.WriteLine(EnvironmentFingerprint.Capture());
        Console.WriteLine($"Artifacts: {MicroBenchmarkConfig.ArtifactsDirectory}");
        Console.WriteLine();

        BenchmarkSwitcher
            .FromAssembly(typeof(MicroBenchmarkProgram).Assembly)
            .Run(args, new MicroBenchmarkConfig());
    }

    private static void WriteFingerprint(string[] args)
    {
        int flagIndex = Array.IndexOf(args, FingerprintFlag);
        string output = flagIndex + 1 < args.Length && !args[flagIndex + 1].StartsWith('-')
            ? args[flagIndex + 1]
            : MicroBenchmarkConfig.ArtifactsDirectory;

        if (!Path.IsPathRooted(output))
            output = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), output));

        EnvironmentFingerprint.WriteArtifact(output);
    }
}
