using BenchmarkDotNet.Running;
using SearchEngine.Sharp.MicroBenchmarks;

if (args.Contains("--fingerprint"))
{
    string output = args.Length > args.ToList().IndexOf("--fingerprint") + 1
        && !args[args.ToList().IndexOf("--fingerprint") + 1].StartsWith('-')
        ? args[args.ToList().IndexOf("--fingerprint") + 1]
        : Path.Combine("artifacts", "microbenchmarks");

    EnvironmentFingerprint.WriteArtifact(output);
    return;
}

Console.WriteLine(EnvironmentFingerprint.Capture());
Console.WriteLine();

var summary = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
_ = summary;
