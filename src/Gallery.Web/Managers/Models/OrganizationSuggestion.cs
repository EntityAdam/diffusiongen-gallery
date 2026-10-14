namespace Gallery.Web.Models;

internal sealed record OrganizationSuggestion(string Id, string Name, string Description, string Tags, string Collection, string Model, string Warning = "");
