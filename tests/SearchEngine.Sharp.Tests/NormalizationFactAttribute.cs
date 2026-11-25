namespace SearchEngine.Sharp.Tests;

// UnicodeMatching.Compose and RemoveDiacritics need Unicode normalization, which globalization-invariant mode
// (DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1, InvariantGlobalization) does not provide: SearchTokenization rejects
// them there. Tests that index with those options are skipped in that mode.
public sealed class NormalizationFactAttribute : FactAttribute
{
    public NormalizationFactAttribute()
    {
        if (!TextFolder.IsNormalizationAvailable)
            Skip = NormalizationTheoryAttribute.SkipReason;
    }
}

public sealed class NormalizationTheoryAttribute : TheoryAttribute
{
    internal const string SkipReason = "Unicode normalization is not available in globalization-invariant mode.";

    public NormalizationTheoryAttribute()
    {
        if (!TextFolder.IsNormalizationAvailable)
            Skip = SkipReason;
    }
}
