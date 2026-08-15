using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;

namespace SearchEngine.Sharp.MicroBenchmarks;

internal static class EnvironmentFingerprint
{
    internal static string Capture(string? gitSha = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["capturedUtc"] = DateTime.UtcNow.ToString("O"),
            ["gitSha"] = gitSha ?? TryGetGitSha(),
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["osDescription"] = RuntimeInformation.OSDescription,
            ["rid"] = RuntimeInformation.RuntimeIdentifier,
            ["processorCount"] = Environment.ProcessorCount,
            ["avx2"] = Avx2.IsSupported,
            ["advSimd"] = AdvSimd.IsSupported,
            ["assembly"] = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    internal static void WriteArtifact(string outputPath, string? gitSha = null)
    {
        string filePath = outputPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? outputPath
            : Path.Combine(outputPath, "environment-fingerprint.json");

        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string json = Capture(gitSha);
        File.WriteAllText(filePath, json);
        Console.WriteLine(json);
    }

    private static string? TryGetGitSha()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            string head = Path.Combine(dir, ".git", "HEAD");
            if (File.Exists(head))
            {
                string content = File.ReadAllText(head).Trim();
                if (content.StartsWith("ref:", StringComparison.Ordinal))
                {
                    string refPath = Path.Combine(dir, ".git", content["ref:".Length..].Trim());
                    if (File.Exists(refPath))
                        return File.ReadAllText(refPath).Trim();
                }

                return content;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        return null;
    }
}
