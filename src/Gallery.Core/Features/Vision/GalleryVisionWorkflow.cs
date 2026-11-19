using System.Collections.Immutable;
using System.Text;
using Gallery.Core.Features.Collections;
using Gallery.Core.Features.Curation;
using Gallery.Core.Features.Media;
using Gallery.Core.Features.Tags;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Features.Vision.Endpoints;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Core.Features.Vision;

internal sealed class GalleryVisionWorkflow(
    GalleryLifetime galleryLifetime, VaultSession vaultSession, VisionManager? visionManager, CollectionOrganizer collectionOrganizer) : IGalleryVision
{
    private const string VisionSettingsId = "vision-settings";
    private const string VisionCheckpointId = "vision-checkpoint";
    private const string VisionCheckpointPurpose = "vision-checkpoint:v1";

    private IGalleryStore galleryStore => galleryLifetime.Current.GalleryStore;

    public Task<IReadOnlyList<string>> GetVisionModelsAsync(VisionSettings settings) =>
        RequiredVisionManager().GetModelsAsync(settings);

    public async Task<(string Description, string Tags)> AnalyzeImageAsync(VisionSettings settings, string imageData, CancellationToken cancellationToken = default)
    {
        var manager = RequiredVisionManager();
        IReadOnlyList<string> names;
        using (var key = vaultSession.AcquireUnlockedSession().Borrow())
            names = await TagCatalogStore.LoadNamesAsync(galleryStore, key.Bytes);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await manager.AnalyzeImageAsync(settings, imageData, names, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return (result.Description, FilterVisionTags(result.Tags, names, settings.UseExistingTags, settings.SuggestNewTags));
    }

    public async Task<MediaSet?> SuggestDestinationSetAsync(string collectionId, IEnumerable<string> mediaIds)
    {
        var catalog = await collectionOrganizer.LoadCollectionCatalogAsync();
        var collection = CollectionOrganizer.RequireCollection(catalog, collectionId);
        var sets = catalog.Sets.Where(set => set.CollectionId == collectionId).ToArray();
        var ids = mediaIds.ToHashSet();
        List<ImageRecord> records;
        using (var key = vaultSession.AcquireUnlockedSession().Borrow())
            records = await galleryStore.ReadRecordsAsync(key.Bytes);
        var images = records.Where(image => ids.Contains(image.Id)).ToArray();
        if (images.Length == 0 || sets.Length == 0) throw new InvalidOperationException("Choose media and a collection with existing sets first.");
        if (images.All(image => string.IsNullOrWhiteSpace(image.Description) && string.IsNullOrWhiteSpace(image.Tags)))
            throw new InvalidOperationException("Analyze or tag the selected media first so the model has content to use.");
        var settings = await LoadSettingsAsync();
        var suggestedId = await RequiredVisionManager().SuggestSetAsync(settings.ForLane(settings.GetLanes()[0]), collection.Name, sets, images);
        return sets.FirstOrDefault(set => set.Id == suggestedId);
    }

    public async Task<VisionSettings> LoadSettingsAsync()
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        return await galleryStore.ReadSettingsAsync<VisionSettings>(VisionSettingsId, VisionSettingsId, key.Bytes) ?? new();
    }

    public async Task SaveSettingsAsync(VisionSettings settings)
    {
        if (settings.Lanes.IsDefaultOrEmpty) VisionService.Validate(settings);
        else
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var lane in settings.Lanes)
            {
                if (string.IsNullOrWhiteSpace(lane.Name) || !names.Add(lane.Name.Trim()))
                    throw new InvalidOperationException("Each vision endpoint needs a unique name.");
                if (lane.MaxConcurrentRequests < 1)
                    throw new InvalidOperationException($"The concurrent request limit for '{lane.Name}' must be at least 1.");
                VisionService.Validate(settings.ForLane(lane));
            }
            settings = settings.ForLane(settings.Lanes[0]);
        }
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        await galleryStore.SaveSettingsAsync(settings, VisionSettingsId, VisionSettingsId, key.Bytes);
    }

    public async Task<VisionCheckpointSession> OpenVisionCheckpointAsync()
    {
        var generation = vaultSession.AcquireUnlockedSession();
        var store = galleryStore;
        VisionCheckpoint state;
        // Serialize reads with writes across unlock generations. A save already in flight at
        // lock time must finish before a newly unlocked UI reads its checkpoint.
        await store.Gate.WaitAsync(generation.RevocationToken);
        try
        {
            using var key = generation.Borrow();
            state = await store.ReadSettingsAsync<VisionCheckpoint>(VisionCheckpointId, VisionCheckpointPurpose, key.Bytes) ?? new();
        }
        finally { store.Gate.Release(); }
        generation.RevocationToken.ThrowIfCancellationRequested();
        return new(state, generation.RevocationToken, async checkpoint =>
        {
            await store.Gate.WaitAsync(generation.RevocationToken);
            try
            {
                using var key = generation.Borrow();
                await store.SaveSettingsAsync(checkpoint, VisionCheckpointId, VisionCheckpointPurpose, key.Bytes);
            }
            finally { store.Gate.Release(); }
        }, draft => ApproveVisionAsync(generation, store, draft.Id, draft.Description, draft.Tags, draft.Model,
            draft.Analysis, draft.TagsOnly, draft.UseExistingTags, draft.SuggestNewTags));
    }

    public async Task<VisionAnalysisResult> AnalyzeForDraftAsync(string id, VisionSettings settings, CancellationToken token = default)
    {
        var generation = vaultSession.AcquireUnlockedSession();
        var store = galleryStore;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, generation.RevocationToken);
        using var key = generation.Borrow();
        var source = await store.ReadRecordAsync(id, key.Bytes);
        await store.Gate.WaitAsync(cancellation.Token);
        IReadOnlyList<string> names;
        try { names = (await TagCatalogStore.ReadAsync(store, key.Bytes)).Names; }
        finally { store.Gate.Release(); }
        cancellation.Token.ThrowIfCancellationRequested();
        var preview = await GalleryMediaReader.ReadPreviewAsync(store, source, key.Bytes, ImageResolution.Fit);
        cancellation.Token.ThrowIfCancellationRequested();
        var result = await RequiredVisionManager().AnalyzeResultAsync(settings, preview, names, cancellation.Token);
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
        return await ApproveVisionAsync(generation, galleryStore, id, description, tags, model,
            analysis, tagsOnly, useExistingTags, suggestNewTags);
    }

    private static async Task<ImageRecord> ApproveVisionAsync(UnlockedVaultSession generation, IGalleryStore store,
        string id, string description, string tags, string model, VisionAnalysis? analysis,
        bool tagsOnly, bool useExistingTags, bool suggestNewTags)
    {
        using var key = generation.Borrow();
        await store.Gate.WaitAsync(generation.RevocationToken);
        try
        {
            var record = await store.ReadRecordAsync(id, key.Bytes);
            var catalog = await TagCatalogStore.ReadAsync(store, key.Bytes);
            if (analysis is not null && !tagsOnly && (record.ContentSha256 != analysis.SourceContentHash
                || record.Description != analysis.SourceDescription || (record.VisionFacts?.RunId ?? "") != analysis.PreviousRunId))
                throw new InvalidOperationException("This analysis is stale. The image, vision notes or accepted analysis changed. Analyze again before approval.");
            var eligible = FilterVisionTags(tags, catalog.Names, useExistingTags, suggestNewTags);
            var updated = record with
            {
                Description = tagsOnly || string.IsNullOrWhiteSpace(description) ? record.Description : description,
                Tags = LibraryRules.MergeTags(record.Tags, eligible),
                AnalysisModel = tagsOnly ? record.AnalysisModel : model,
                VisionFacts = !tagsOnly && analysis is not null ? analysis.Facts : record.VisionFacts
            };
            var nextCatalog = new TagCatalog(TagCatalogStore.NormalizeTagNames(catalog.Names.Concat(TagCatalogStore.TagsOf(updated))));
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

    private VisionManager RequiredVisionManager() => visionManager
        ?? throw new InvalidOperationException("The vision manager is not configured.");
}
