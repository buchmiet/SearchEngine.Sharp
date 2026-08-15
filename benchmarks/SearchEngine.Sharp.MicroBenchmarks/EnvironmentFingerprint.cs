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

    internal static void WriteArtifact(string outputDirectory, string? gitSha = null)
    {
        Directory.CreateDirectory(outputDirectory);
        string json = Capture(gitSha);
        File.WriteAllText(Path.Combine(outputDirectory, "environment-fingerprint.json"), json);
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
