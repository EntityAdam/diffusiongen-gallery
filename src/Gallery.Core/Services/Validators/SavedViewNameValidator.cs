using System.Collections.Immutable;
using Gallery.Core.Exceptions;

namespace Gallery.Core.Services.Validators;

internal static class SavedViewNameValidator
{
    private const int MinimumNameLength = 1;
    private const int MaximumNameLength = 80;

    public static void Validate(string name)
    {
        if (name.Length < MinimumNameLength || name.Length > MaximumNameLength)
            throw new InvalidSavedViewNameException(
                $"Saved view name '{name}' must have {MinimumNameLength} to {MaximumNameLength} characters.",
                GalleryLogEvent.ValidateSavedViewName, new ContextualData(ImmutableDictionary<string, object>.Empty
                    .Add("SavedView.Name", name)
                    .Add("SavedView.MinimumNameLength", MinimumNameLength)
                    .Add("SavedView.MaximumNameLength", MaximumNameLength)));
    }
}
