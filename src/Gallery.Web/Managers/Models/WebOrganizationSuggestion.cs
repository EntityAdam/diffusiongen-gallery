namespace Gallery.Web.Models;

internal sealed record WebOrganizationSuggestion(string Id, string Name, string Description, string Tags, string Collection, string Model, string Warning = "");
