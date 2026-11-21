using Gallery.Core.Exceptions;
using Gallery.Core.Shared.Diagnostics;

namespace Gallery.Core.Features.Settings;

internal static class SavedViewNameValidator
{
    private const int MinimumNameLength = 1;
    private const int MaximumNameLength = 80;

    public static void Validate(string name)
    {
        if (!HasSupportedLength(name))
            throw new InvalidSavedViewNameException(
                $"Saved view name '{name}' must have {MinimumNameLength} to {MaximumNameLength} characters.",
                GalleryLogEvent.ValidateSavedViewName, new ContextualData()
                    .Add("SavedView.Name", name)
                    .Add("SavedView.MinimumNameLength", MinimumNameLength)
                    .Add("SavedView.MaximumNameLength", MaximumNameLength));
    }

    private static bool HasSupportedLength(string name) =>
        name.Length is >= MinimumNameLength and <= MaximumNameLength;
}
