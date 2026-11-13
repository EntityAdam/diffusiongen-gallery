namespace Gallery.Web.Components;

public sealed record OrganizationSuggestion(string Id, string Name, string Description, string Tags, string Model, string Warning = "", Gallery.Core.Models.VisionAnalysis? Analysis = null, bool UseExistingTags = true, bool SuggestNewTags = true);
