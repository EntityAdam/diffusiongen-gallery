using Gallery.Core.Models;
using Gallery.Core.Services.Processors;
using System.Collections.Immutable;
using System.Text;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    public async Task<VisionAnalysisResult> AnalyzeForDraftAsync(string id, VisionSettings settings, bool includeCollection = false,
        IEnumerable<string>? collections = null, CancellationToken token = default)
    {
        var generation = vaultSession.AcquireUnlockedSession();
        var store = galleryStore;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, generation.RevocationToken);
        using var key = generation.Borrow();
        var source = await store.ReadRecordAsync(id, key.Bytes);
        await store.Gate.WaitAsync(cancellation.Token);
        IReadOnlyList<string> names;
        try { names = (await ReadTagCatalogWithKeyAsync(key.Bytes, store: store)).Names; }
        finally { store.Gate.Release(); }
        cancellation.Token.ThrowIfCancellationRequested();
        string preview;
        if (source.IsVideo)
        {
            using var thumbnail = await store.ReadThumbnailAsync(id, key.Bytes);
            preview = PngDataUrlPrefix + Convert.ToBase64String(thumbnail.Content.Span);
        }
        else preview = await GalleryImageRenderer.CreatePreviewAsync(source,
            await StoredMediaProcessor.ReadAsync(source, key.Bytes), ImageResolution.Fit);
        cancellation.Token.ThrowIfCancellationRequested();
        var result = await RequiredVisionManager().AnalyzeResultAsync(settings, preview, includeCollection ? collections ?? [] : null, names, cancellation.Token);
        cancellation.Token.ThrowIfCancellationRequested();
        var tags = FilterVisionTags(result.Tags, names, settings.UseExistingTags, settings.SuggestNewTags);
        var warning = result.Warning;
        if (tags != result.Tags) warning = (warning + " Tag assignments were filtered/canonicalized using the selected tag settings.").Trim();
        var analysis = result.Analysis is null ? null : result.Analysis with
        {
            ExistingTagCandidates = settings.UseExistingTags ? names.ToImmutableArray() : [],
            ImageInput = source.IsVideo ? "video-thumbnail" : "fit-preview",
            SourceContentHash = source.ContentSha256, SourceDescription = source.Description,
            PreviousRunId = source.VisionFacts?.RunId ?? "",
            Facts = result.Analysis.Facts with { SourceContentHash = source.ContentSha256 }
        };
        return result with { Tags = tags, Warning = warning, Analysis = analysis };
    }

    private static string FilterVisionTags(string tags, IReadOnlyList<string> names, bool useExisting, bool suggestNew)
    {
        var known = names.ToDictionary(name => name, StringComparer.OrdinalIgnoreCase);
        return string.Join(", ", tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tag => tag.Length <= 100 && !tag.Any(char.IsControl))
            .Where(tag => known.ContainsKey(tag) ? useExisting : suggestNew)
            .Select(tag => known.GetValueOrDefault(tag, tag)).Distinct(StringComparer.OrdinalIgnoreCase).Take(100));
    }

    public async Task<ImageRecord> ApproveVisionAsync(string id, string description, string tags, string model,
        VisionAnalysis? analysis, bool tagsOnly = false, bool useExistingTags = true, bool suggestNewTags = true)
    {
        var generation = vaultSession.AcquireUnlockedSession();
        var store = galleryStore;
        using var key = generation.Borrow();
        await store.Gate.WaitAsync(generation.RevocationToken);
        try
        {
            var record = await store.ReadRecordAsync(id, key.Bytes);
            var catalog = await ReadTagCatalogWithKeyAsync(key.Bytes, store: store);
            if (analysis is not null && !tagsOnly && (record.ContentSha256 != analysis.SourceContentHash
                || record.Description != analysis.SourceDescription || (record.VisionFacts?.RunId ?? "") != analysis.PreviousRunId))
                throw new InvalidOperationException("This analysis is stale. The image, vision notes or accepted analysis changed. Analyze again before approval.");
            var eligible = FilterVisionTags(tags, catalog.Names, useExistingTags, suggestNewTags);
            var updated = record with
            {
                Description = tagsOnly || string.IsNullOrWhiteSpace(description) ? record.Description : description,
                Tags = MergeTags(record.Tags, eligible),
                AnalysisModel = tagsOnly ? record.AnalysisModel : model,
                VisionFacts = !tagsOnly && analysis is not null ? analysis.Facts : record.VisionFacts
            };
            var nextCatalog = new TagCatalog(NormalizeTagNames(catalog.Names.Concat(TagsOf(updated))));
            generation.RevocationToken.ThrowIfCancellationRequested();
            if (analysis is not null && !tagsOnly) await store.CommitVisionAnalysisAsync(updated, nextCatalog, analysis, key.Bytes);
            else await store.CommitTagCatalogAsync([updated], nextCatalog, null, key.Bytes);
            return updated;
        }
        finally { store.Gate.Release(); }
    }

    public async Task<VisionAnalysis?> ReadVisionAnalysisAsync(string id)
    {
        var generation = vaultSession.AcquireUnlockedSession();
        var store = galleryStore;
        using var key = generation.Borrow();
        await store.Gate.WaitAsync(generation.RevocationToken);
        try
        {
            var record = await store.ReadRecordAsync(id, key.Bytes);
            var analysis = await store.ReadVisionAnalysisAsync(id, key.Bytes);
            generation.RevocationToken.ThrowIfCancellationRequested();
            return analysis?.SourceContentHash == record.ContentSha256 && analysis.RunId == record.VisionFacts?.RunId ? analysis : null;
        }
        finally { store.Gate.Release(); }
    }

    public async Task<ExportFile> ExportVisionAnalysisAsync(string id)
    {
        var analysis = await ReadVisionAnalysisAsync(id) ?? throw new InvalidOperationException("No accepted structured analysis is available.");
        return new($"{id}.analysis.json", "application/json", Encoding.UTF8.GetBytes(analysis.Json));
    }
}
