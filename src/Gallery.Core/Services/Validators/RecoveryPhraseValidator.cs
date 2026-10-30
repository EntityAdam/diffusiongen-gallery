using Gallery.Core.Models;

namespace Gallery.Core.Services.Validators;

internal static class RecoveryPhraseValidator
{
    public static string Normalize(string phrase)
    {
        var words = ParseWords(phrase);
        ValidatePhrase(words);
        return string.Join(' ', words);
    }

    private static string[] ParseWords(string phrase) =>
        (phrase ?? "").ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static void ValidatePhrase(string[] words)
    {
        if (!IsValidPhrase(words))
            throw new InvalidOperationException($"Recovery requires all {RecoveryPhraseWords.Count} compound words in their original order.");
    }

    private static bool IsValidPhrase(string[] words) =>
        words.Length == RecoveryPhraseWords.Count && words.All(IsValidWord);

    private static bool IsValidWord(string word)
    {
        var parts = word.Split('-');
        return parts.Length == 2 && RecoveryPhraseWords.Prefixes.Contains(parts[0]) && RecoveryPhraseWords.Suffixes.Contains(parts[1]);
    }
}
