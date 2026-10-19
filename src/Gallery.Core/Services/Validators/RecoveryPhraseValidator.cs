using Gallery.Core.Models;

namespace Gallery.Core.Services.Validators;

internal static class RecoveryPhraseValidator
{
    public static string Normalize(string phrase)
    {
        var words = (phrase ?? "").ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length != RecoveryPhraseWords.Count || !words.All(IsValidWord))
            throw new InvalidOperationException($"Recovery requires all {RecoveryPhraseWords.Count} compound words in their original order.");
        return string.Join(' ', words);
    }

    private static bool IsValidWord(string word)
    {
        var parts = word.Split('-');
        return parts.Length == 2 && RecoveryPhraseWords.Prefixes.Contains(parts[0]) && RecoveryPhraseWords.Suffixes.Contains(parts[1]);
    }
}
