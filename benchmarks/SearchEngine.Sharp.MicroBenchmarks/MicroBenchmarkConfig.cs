using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using BenchmarkDotNet.Validators;

namespace SearchEngine.Sharp.MicroBenchmarks;

internal sealed class MicroBenchmarkConfig : ManualConfig
{
    internal static string ArtifactsDirectory { get; } = ResolveArtifactsDirectory();

    public MicroBenchmarkConfig()
    {
        Directory.CreateDirectory(ArtifactsDirectory);
        Environment.SetEnvironmentVariable("BENCHMARKDOTNET_ARTIFACTS", ArtifactsDirectory);

        AddLogger(ConsoleLogger.Default);
        AddExporter(MarkdownExporter.GitHub);
        AddExporter(CsvExporter.Default);
        AddExporter(HtmlExporter.Default);
        AddColumnProvider(DefaultColumnProviders.Instance);
        AddValidator(JitOptimizationsValidator.DontFailOnError);
        AddAnalyser(EnvironmentAnalyser.Default);

        WithArtifactsPath(ArtifactsDirectory);

        var inProcess = InProcessNoEmitToolchain.Instance;
        AddJob(Job.Default.WithToolchain(inProcess));
    }

    private static string ResolveArtifactsDirectory()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "SearchEngine.Sharp.MicroBenchmarks.csproj")))
                return Path.Combine(dir, "BenchmarkDotNet.Artifacts");

            dir = Directory.GetParent(dir)?.FullName;
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "BenchmarkDotNet.Artifacts");
    }
}
