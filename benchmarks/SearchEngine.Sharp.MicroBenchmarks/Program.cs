using BenchmarkDotNet.Running;
using SearchEngine.Sharp.MicroBenchmarks;

if (args.Contains("--fingerprint"))
{
    int flagIndex = Array.IndexOf(args, "--fingerprint");
    string output = flagIndex >= 0
        && flagIndex + 1 < args.Length
        && !args[flagIndex + 1].StartsWith('-')
        ? args[flagIndex + 1]
        : MicroBenchmarkConfig.ArtifactsDirectory;

    if (!Path.IsPathRooted(output))
        output = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), output));

    EnvironmentFingerprint.WriteArtifact(output);
    return;
}

Console.WriteLine(EnvironmentFingerprint.Capture());
Console.WriteLine($"Artifacts: {MicroBenchmarkConfig.ArtifactsDirectory}");
Console.WriteLine();

var summary = BenchmarkSwitcher
    .FromAssembly(typeof(Program).Assembly)
    .Run(args, new MicroBenchmarkConfig());

_ = summary;
