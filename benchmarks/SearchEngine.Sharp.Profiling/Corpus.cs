using System.Text;

namespace SearchEngine.Profiling;

/// <summary>One synthetic file: name, relative directory, size and modification time.</summary>
public readonly record struct SynthFile(string Name, int DirectoryIndex, long Size, long ModifiedTicks);

/// <summary>
/// Deterministic generator of realistic file names (extension mix, numbered series,
/// shared prefixes, mixed scripts, a share of decomposed (NFD) accents) and directory layout.
/// Same seed and count always give the same corpus on every machine.
/// </summary>
public static class Corpus
{
    static readonly string[] Words =
    [
        "report", "invoice", "summary", "backup", "config", "settings", "user", "service", "manager", "handler",
        "search", "index", "engine", "query", "result", "file", "folder", "panel", "view", "model",
        "controller", "factory", "provider", "builder", "parser", "reader", "writer", "stream", "buffer", "cache",
        "test", "tests", "spec", "mock", "helper", "util", "common", "core", "data", "client",
        "server", "network", "socket", "session", "token", "auth", "login", "profile", "account", "order",
        "payment", "product", "catalog", "price", "budget", "plan", "draft", "final", "notes", "meeting",
        "project", "design", "layout", "theme", "icon", "image", "photo", "video", "audio", "music",
        "document", "letter", "contract", "agreement", "proposal", "presentation", "chart", "table", "sheet", "export",
    ];

    static readonly string[] CodeExt = [".cs", ".cs", ".cs", ".ts", ".js", ".py", ".h", ".c", ".cpp", ".go", ".rs", ".java", ".swift", ".kt"];
    static readonly string[] DocExt = [".pdf", ".pdf", ".docx", ".xlsx", ".pptx", ".txt", ".md", ".odt", ".rtf", ".csv"];
    static readonly string[] DataExt = [".json", ".xml", ".yml", ".yaml", ".toml", ".ini", ".lock", ".props", ".config", ".plist"];
    static readonly string[] LibExt = [".dll", ".so", ".dylib", ".a", ".o", ".pdb", ".nupkg", ".jar", ".whl"];
    static readonly string[] MediaExt = [".mp3", ".flac", ".mp4", ".mkv", ".mov", ".wav", ".m4a"];
    static readonly string[] ImgExt = [".jpg", ".JPG", ".jpeg", ".png", ".heic", ".gif", ".webp", ".svg"];

    // Composed (NFC) multilingual stems; a share of them is emitted in NFD form.
    static readonly string[] Intl =
    [
        "Zażółć gęślą jaźń", "Sprawozdanie roczne", "Faktura końcowa", "Łódź wycieczka", "Kraków zdjęcia",
        "Straße Übersicht", "Größenänderung", "Café Crème", "résumé", "naïve façade", "Ångström",
        "Σημειώσεις ταξιδιού", "ΟΔΥΣΣΕΥΣ", "Отчёт за год", "Ёлка", "写真", "会議メモ", "회의록", "한국어 문서",
        "İstanbul ıssız", "DIŞ İŞLER", "Ελληνικά σίγμα", "Mañana", "São Paulo", "Ærø", "Øresund",
        "🎉 party", "👨‍👩‍👧 family", "🇵🇱 flaga", "naïveté", "Œuvre",
    ];

    static readonly string[] DirWords =
    [
        "src", "lib", "bin", "obj", "docs", "assets", "images", "photos", "music", "videos", "Downloads", "Documents",
        "Projects", "archive", "2023", "2024", "2025", "backup", "tmp", "cache", "node_modules", "packages", "include",
        "share", "test", "tests", "build", "out", "Release", "Debug", "Desktop", "Library", "Resources", "data", "logs",
    ];

    /// <summary>Directory layout: parent index per directory and its name (index 0 = root, name "").</summary>
    public sealed record Layout(int[] Parent, string[] Name)
    {
        public string RelativePath(int dir)
        {
            var parts = new List<string>();
            for (int d = dir; d > 0; d = Parent[d]) parts.Add(Name[d]);
            parts.Reverse();
            return string.Join('/', parts);
        }
    }

    public static (Layout Layout, SynthFile[] Files) Generate(int count, int seed)
    {
        var rng = new Random(seed);
        // About 14 files per directory on average, depth up to 9.
        int dirCount = Math.Max(1, count / 14);
        var parent = new int[dirCount];
        var dirName = new string[dirCount];
        var depth = new int[dirCount];
        dirName[0] = "";
        for (int d = 1; d < dirCount; d++)
        {
            // Prefer recent directories as parents (deep, bushy trees), cap depth.
            int p = rng.Next(Math.Max(0, d - 200), d);
            int maxDepth = 3 + rng.Next(7); // depth spread 3..9
            while (depth[p] >= maxDepth) p = parent[p];
            parent[d] = p;
            depth[d] = depth[p] + 1;
            dirName[d] = rng.Next(4) == 0
                ? Pick(rng, DirWords) + "_" + d.ToString("x")
                : Pick(rng, DirWords) + d.ToString();
        }

        var anchor = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var files = new SynthFile[count];
        var seriesCounter = new int[8];
        for (int i = 0; i < count; i++)
        {
            string name = MakeName(rng, seriesCounter, i);
            // Log-normal sizes, 3% empty files.
            long size = rng.Next(100) < 3 ? 0 : (long)Math.Min(4L << 30, Math.Exp(8.5 + 2.6 * Gaussian(rng)));
            // Modification times skewed toward recent: 5 years, quadratic.
            double u = rng.NextDouble();
            long modified = anchor - (long)(u * u * 5 * 365 * TimeSpan.TicksPerDay) - rng.Next(86_400) * TimeSpan.TicksPerSecond;
            files[i] = new SynthFile(name, rng.Next(dirCount), size, modified);
        }

        return (new Layout(parent, dirName), files);
    }

    static string MakeName(Random rng, int[] series, int i)
    {
        int bucket = rng.Next(100);
        if (bucket < 16) // camera and screenshot series
        {
            return rng.Next(4) switch
            {
                0 => $"IMG_{++series[0] % 10000:D4}{Pick(rng, ImgExt)}",
                1 => $"DSC{++series[1] % 100000:D5}.JPG",
                2 => $"PXL_2024{rng.Next(1, 13):D2}{rng.Next(1, 29):D2}_{rng.Next(240000):D6}{rng.Next(1000):D3}.jpg",
                _ => $"Screenshot 2025-{rng.Next(1, 13):D2}-{rng.Next(1, 29):D2} at {rng.Next(24)}.{rng.Next(60):D2}.{rng.Next(60):D2}.png",
            };
        }
        if (bucket < 38) // source code, PascalCase compounds
        {
            var sb = new StringBuilder();
            int parts = rng.Next(1, 4);
            for (int p = 0; p < parts; p++) sb.Append(Capitalize(Pick(rng, Words)));
            if (rng.Next(6) == 0) sb.Append("Tests");
            return sb + Pick(rng, CodeExt);
        }
        if (bucket < 48) // office documents
        {
            return rng.Next(5) switch
            {
                0 => $"invoice-{rng.Next(100000):D5}.pdf",
                1 => $"{Capitalize(Pick(rng, Words))} {Pick(rng, Words)} Q{rng.Next(1, 5)} {rng.Next(2019, 2027)}{(rng.Next(3) == 0 ? " final" : "")}{(rng.Next(5) == 0 ? $" ({rng.Next(1, 9)})" : "")}{Pick(rng, DocExt)}",
                2 => $"{Pick(rng, Words)}_{Pick(rng, Words)}_v{rng.Next(1, 20)}{Pick(rng, DocExt)}",
                3 => $"{Capitalize(Pick(rng, Words))} - Copy{Pick(rng, DocExt)}",
                _ => $"{Pick(rng, Words)}-{rng.Next(2019, 2027)}-{rng.Next(1, 13):D2}-{rng.Next(1, 29):D2}{Pick(rng, DocExt)}",
            };
        }
        if (bucket < 58) // libraries
        {
            return rng.Next(3) switch
            {
                0 => $"lib{Pick(rng, Words)}{(rng.Next(2) == 0 ? "-" + Pick(rng, Words) : "")}.so.{rng.Next(1, 4)}.{rng.Next(10)}.{rng.Next(20)}",
                1 => $"System.{Capitalize(Pick(rng, Words))}.{Capitalize(Pick(rng, Words))}.dll",
                _ => $"{Pick(rng, Words)}{(rng.Next(2) == 0 ? "_" + Pick(rng, Words) : "")}{Pick(rng, LibExt)}",
            };
        }
        if (bucket < 67) // hashes and caches
        {
            int len = rng.Next(4) == 0 ? 40 : rng.Next(8, 33);
            var chars = new char[len];
            for (int c = 0; c < len; c++) chars[c] = "0123456789abcdef"[rng.Next(16)];
            string ext = rng.Next(3) switch { 0 => "", 1 => ".tmp", _ => rng.Next(2) == 0 ? ".pack" : ".idx" };
            return new string(chars) + ext;
        }
        if (bucket < 75) // config and data, some dotfiles
        {
            return rng.Next(6) switch
            {
                0 => "." + Pick(rng, ["gitignore", "editorconfig", "DS_Store", "npmrc", "env", "bashrc"]),
                1 => "package.json",
                _ => $"{Pick(rng, Words)}{(rng.Next(2) == 0 ? "." + Pick(rng, Words) : "")}{Pick(rng, DataExt)}",
            };
        }
        if (bucket < 82) // media
        {
            return rng.Next(2) == 0
                ? $"{rng.Next(1, 20):D2} - {Capitalize(Pick(rng, Words))} - {Capitalize(Pick(rng, Words))} {Pick(rng, Words)}{Pick(rng, MediaExt)}"
                : $"{Capitalize(Pick(rng, Words))}.{Capitalize(Pick(rng, Words))}.{rng.Next(1990, 2026)}.1080p{Pick(rng, MediaExt)}";
        }
        if (bucket < 88) // logs
        {
            return rng.Next(2) == 0
                ? $"{Pick(rng, Words)}-{rng.Next(2023, 2027)}-{rng.Next(1, 13):D2}-{rng.Next(1, 29):D2}.log"
                : $"{Pick(rng, Words)}.log.{rng.Next(1, 10)}";
        }
        // Mixed scripts; 40% decomposed (NFD), as macOS tools and some archives produce them.
        string stem = Pick(rng, Intl) + (rng.Next(2) == 0 ? $" {rng.Next(1, 100)}" : "");
        string ext2 = rng.Next(2) == 0 ? Pick(rng, DocExt) : Pick(rng, ImgExt);
        string intlName = stem + ext2;
        return rng.Next(10) < 4 ? intlName.Normalize(NormalizationForm.FormD) : intlName;
    }

    static string Pick(Random rng, string[] items) => items[rng.Next(items.Length)];
    static string Capitalize(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>
    /// Writes the corpus to disk under <paramref name="root"/>: directories, empty files whose
    /// logical size is set with SetLength (sparse, no data written) and the modification time.
    /// Duplicate names in one directory are suffixed so every synthetic file exists once.
    /// </summary>
    public static (int Files, int Dirs) Materialize(string root, int count, int seed, bool setSizeAndTime)
    {
        var (layout, files) = Generate(count, seed);
        var dirPaths = new string[layout.Parent.Length];
        dirPaths[0] = root;
        Directory.CreateDirectory(root);
        for (int d = 1; d < dirPaths.Length; d++)
        {
            dirPaths[d] = Path.Combine(dirPaths[layout.Parent[d]], layout.Name[d]);
            Directory.CreateDirectory(dirPaths[d]);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int written = 0;
        foreach (var f in files)
        {
            string path = Path.Combine(dirPaths[f.DirectoryIndex], f.Name);
            // APFS is normalization-insensitive (NFC and NFD name the same file), so dedupe on NFC.
            if (!seen.Add(path.Normalize(NormalizationForm.FormC)))
                path = Path.Combine(dirPaths[f.DirectoryIndex], $"{written}_{f.Name}");
            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                if (setSizeAndTime && f.Size > 0) fs.SetLength(f.Size);
            }
            if (setSizeAndTime) File.SetLastWriteTimeUtc(path, new DateTime(f.ModifiedTicks, DateTimeKind.Utc));
            written++;
        }
        return (written, dirPaths.Length);
    }
}
