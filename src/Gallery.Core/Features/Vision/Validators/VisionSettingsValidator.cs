using Gallery.Core.Features.Vision.Endpoints;
using Gallery.Core.Models;

namespace Gallery.Core.Features.Vision.Validators;

internal static class VisionSettingsValidator
{
    public static VisionSettings ValidateAndNormalize(VisionSettings settings)
    {
        if (settings.Lanes.IsDefaultOrEmpty)
        {
            VisionService.Validate(settings);
            return settings;
        }

        var laneNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lane in settings.Lanes)
        {
            if (string.IsNullOrWhiteSpace(lane.Name) || !laneNames.Add(lane.Name.Trim()))
                throw new InvalidOperationException("Each vision endpoint needs a unique name.");
            if (lane.MaxConcurrentRequests < 1)
                throw new InvalidOperationException($"The concurrent request limit for '{lane.Name}' must be at least 1.");
            VisionService.Validate(settings.ForLane(lane));
        }
        return settings.ForLane(settings.Lanes[0]);
    }
}
