using System.Collections.Immutable;
using Gallery.Core.Models;

namespace Gallery.Core.Services.Processors;

internal static class DiffusionModelRules
{
    public static List<DiffusionModelGroup> GroupByDiffusionModel(IEnumerable<ImageRecord> orderedImages)
    {
        var groupsByModel = new Dictionary<string, List<ImageRecord>>(StringComparer.OrdinalIgnoreCase);
        var orderedModels = new List<string>();
        var imagesWithoutModel = new List<ImageRecord>();

        foreach (var image in orderedImages)
        {
            var model = image.Generation?.Model.Trim() ?? "";
            if (model.Length == 0)
            {
                imagesWithoutModel.Add(image);
                continue;
            }

            if (!groupsByModel.TryGetValue(model, out var modelImages))
            {
                modelImages = [];
                groupsByModel.Add(model, modelImages);
                orderedModels.Add(model);
            }
            modelImages.Add(image);
        }

        var groups = orderedModels
            .Select((model, order) => (model, order, Images: groupsByModel[model]))
            .OrderByDescending(group => group.Images.Count)
            .ThenBy(group => group.order)
            .Select(group => new DiffusionModelGroup(group.model, group.Images.ToImmutableArray()))
            .ToList();
        if (imagesWithoutModel.Count > 0)
            groups.Add(new("", imagesWithoutModel.ToImmutableArray()));
        return groups;
    }
}
