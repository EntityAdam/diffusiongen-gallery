using System.Collections.Immutable;
using Gallery.Core.Exceptions;
using Gallery.Core.Features.Diagnostics.Models;

namespace Gallery.Core.Features.Settings;

internal static class SavedViewNameValidator
{
    private const int MinimumNameLength = 1;
    private const int MaximumNameLength = 80;

    public static string NormalizeAndValidate(string name)
    {
        name = name.Trim();
        if (!HasSupportedLength(name))
            throw new InvalidSavedViewNameException(
                $"Saved view name '{name}' must have {MinimumNameLength} to {MaximumNameLength} characters.",
                LogEventGallery.ValidateSavedViewName, new ContextualData(ImmutableDictionary<string, object>.Empty
                    .Add("SavedView.Name", name)
                    .Add("SavedView.MinimumNameLength", MinimumNameLength)
                    .Add("SavedView.MaximumNameLength", MaximumNameLength)));
        return name;
    }

    private static bool HasSupportedLength(string name) =>
        name.Length is >= MinimumNameLength and <= MaximumNameLength;
}
