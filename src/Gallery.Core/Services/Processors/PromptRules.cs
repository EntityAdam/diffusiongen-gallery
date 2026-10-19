using System.Collections.Immutable;
using Gallery.Core.Models;

namespace Gallery.Core.Services.Processors;

internal static class PromptRules
{
    /// <summary>Groups exactly matching prompts (shared groups first, largest first), then unique and missing prompts.</summary>
    public static List<PromptGroup> GroupByPrompt(IEnumerable<ImageRecord> orderedImages)
    {
        List<(string Prompt, List<ImageRecord> Images)> promptGroups;
        List<ImageRecord> imagesWithoutPrompts;
        (promptGroups, imagesWithoutPrompts) = CollectPromptGroups(orderedImages);
        var sharedPromptGroups = OrderSharedPromptGroups(promptGroups);
        var orderedPromptGroups = new List<PromptGroup>(sharedPromptGroups);
        AppendUniquelyPromptedImages(orderedPromptGroups, promptGroups);
        AppendImagesWithoutPrompts(orderedPromptGroups, imagesWithoutPrompts);
        return orderedPromptGroups;
    }

    private static (List<(string Prompt, List<ImageRecord> Images)> PromptGroups, List<ImageRecord> ImagesWithoutPrompts)
        CollectPromptGroups(IEnumerable<ImageRecord> orderedImages)
    {
        var promptGroups = new List<(string Prompt, List<ImageRecord> Images)>();
        var promptGroupsByPrompt = new Dictionary<string, List<ImageRecord>>(StringComparer.Ordinal);
        var imagesWithoutPrompts = new List<ImageRecord>();
        foreach (var imageRecord in orderedImages)
        {
            var prompt = imageRecord.Prompt;
            if (prompt.Length == 0) { imagesWithoutPrompts.Add(imageRecord); continue; }
            if (!promptGroupsByPrompt.TryGetValue(prompt, out var promptGroupImages))
            {
                promptGroupImages = [];
                promptGroupsByPrompt[prompt] = promptGroupImages;
                promptGroups.Add((prompt, promptGroupImages));
            }
            promptGroupImages.Add(imageRecord);
        }
        return (promptGroups, imagesWithoutPrompts);
    }

    private static List<PromptGroup> OrderSharedPromptGroups(
        List<(string Prompt, List<ImageRecord> Images)> promptGroups) =>
        promptGroups.Where(promptGroup => promptGroup.Images.Count > 1)
            .Select((promptGroup, order) => (promptGroup, order))
            .OrderByDescending(item => item.promptGroup.Images.Count).ThenBy(item => item.order)
            .Select(item => new PromptGroup(PromptGroup.Shared, item.promptGroup.Prompt, item.promptGroup.Images.ToImmutableArray()))
            .ToList();

    private static void AppendUniquelyPromptedImages(List<PromptGroup> orderedPromptGroups,
        List<(string Prompt, List<ImageRecord> Images)> promptGroups)
    {
        var uniquelyPromptedImages = promptGroups.Where(promptGroup => promptGroup.Images.Count == 1)
            .SelectMany(promptGroup => promptGroup.Images).ToList();
        if (uniquelyPromptedImages.Count > 0)
            orderedPromptGroups.Add(new(PromptGroup.Unique, "", uniquelyPromptedImages.ToImmutableArray()));
    }

    private static void AppendImagesWithoutPrompts(List<PromptGroup> orderedPromptGroups,
        List<ImageRecord> imagesWithoutPrompts)
    {
        if (imagesWithoutPrompts.Count > 0)
            orderedPromptGroups.Add(new(PromptGroup.None, "", imagesWithoutPrompts.ToImmutableArray()));
    }

}
