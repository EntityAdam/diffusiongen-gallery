using System.Collections.Immutable;
using Gallery.Core.Models;

namespace Gallery.Core.Services.Processors;

internal static class DiffusionModelRules
{
    public static List<DiffusionModelGroup> GroupByDiffusionModel(IEnumerable<ImageRecord> orderedImages)
    {
        Dictionary<string, List<ImageRecord>> imagesByModel;
        List<string> orderedModels;
        List<ImageRecord> imagesWithoutModel;
        (imagesByModel, orderedModels, imagesWithoutModel) = CollectModelBuckets(orderedImages);
        var diffusionModelGroups = OrderModelGroups(imagesByModel, orderedModels);
        if (imagesWithoutModel.Count > 0)
            diffusionModelGroups.Add(new("", imagesWithoutModel.ToImmutableArray()));
        return diffusionModelGroups;
    }

    private static (Dictionary<string, List<ImageRecord>> ImagesByModel, List<string> OrderedModels,
        List<ImageRecord> ImagesWithoutModel) CollectModelBuckets(IEnumerable<ImageRecord> orderedImages)
    {
        var imagesByModel = new Dictionary<string, List<ImageRecord>>(StringComparer.OrdinalIgnoreCase);
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

            if (!imagesByModel.TryGetValue(model, out var modelImages))
            {
                modelImages = [];
                imagesByModel.Add(model, modelImages);
                orderedModels.Add(model);
            }
            modelImages.Add(image);
        }
        return (imagesByModel, orderedModels, imagesWithoutModel);
    }

    private static List<DiffusionModelGroup> OrderModelGroups(
        Dictionary<string, List<ImageRecord>> imagesByModel, List<string> orderedModels) =>
        orderedModels
            .Select((model, order) => (model, order, Images: imagesByModel[model]))
            .OrderByDescending(group => group.Images.Count)
            .ThenBy(group => group.order)
            .Select(group => new DiffusionModelGroup(group.model, group.Images.ToImmutableArray()))
            .ToList();
}
