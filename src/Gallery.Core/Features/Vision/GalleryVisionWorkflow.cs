using System.Collections.Immutable;
using System.Text;
using Gallery.Core.Features.Collections;
using Gallery.Core.Features.Curation;
using Gallery.Core.Features.Media;
using Gallery.Core.Features.Tags;
using Gallery.Core.Features.Tags.CatalogStores;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Features.Vision.Endpoints;
using Gallery.Core.Features.Vision.Validators;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Core.Features.Vision;

internal sealed class GalleryVisionWorkflow(
    GalleryLifetime galleryLifetime, VaultSession vaultSession, VisionManager? visionManager, CollectionOrganizer collectionOrganizer) : IGalleryVision
{
    [Flags]
    private enum VisionTagSources
    {
        None = 0,
        Existing = 1,
        Suggested = 2
    }

    private enum VisionApprovalMode
    {
        Full,
        TagsOnly
    }

    private const string VisionSettingsId = "vision-settings";
    private readonly SemaphoreSlim checkpointSessionGate = new(1, 1);
    private UnlockedVaultSession? checkpointGeneration;
    private VisionCheckpointSession? checkpointSession;

    private IGalleryStore galleryStore => galleryLifetime.Current.GalleryStore;

    public Task<IReadOnlyList<string>> GetVisionModelsAsync(VisionSettings settings) =>
        RequiredVisionManager().GetModelsAsync(settings);

    public async Task<(string Description, string Tags)> AnalyzeImageAsync(VisionSettings settings, string imageData, CancellationToken cancellationToken = default)
    {
        var manager = RequiredVisionManager();
        IReadOnlyList<string> names;
        using (var keyLease = vaultSession.AcquireUnlockedSession().Borrow())
            names = await TagCatalogStore.LoadNamesAsync(galleryStore, keyLease.Bytes);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await manager.AnalyzeImageAsync(settings, imageData, names, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return (result.Description, FilterVisionTags(result.Tags, names, TagSources(settings.UseExistingTags, settings.SuggestNewTags)));
    }

    public async Task<MediaSet?> SuggestDestinationSetAsync(string collectionId, IEnumerable<string> mediaIds)
    {
        var generation = vaultSession.AcquireUnlockedSession();
        var store = galleryStore;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(generation.RevocationToken);
        var catalog = await collectionOrganizer.LoadCollectionCatalogAsync();
        cancellation.Token.ThrowIfCancellationRequested();
        var suggestionInputs = await LoadSetSuggestionInputsAsync(generation, store, cancellation.Token);
        cancellation.Token.ThrowIfCancellationRequested();
        var collection = RequireSuggestionCollection(catalog, collectionId);
        var sets = FindSuggestionSets(catalog, collectionId);
        var images = SelectSuggestionImages(suggestionInputs.Records, mediaIds);
        EnsureSuggestionInputsAreUsable(images, sets);
        var settings = suggestionInputs.Settings;
        var suggestedId = await RequiredVisionManager().SuggestSetAsync(settings.ForLane(settings.GetLanes()[0]),
            collection.Name, sets, images, cancellation.Token);
        cancellation.Token.ThrowIfCancellationRequested();
        return FindSuggestedSet(sets, suggestedId);
    }

    private static CollectionDefinition RequireSuggestionCollection(CollectionCatalog catalog, string collectionId) =>
        CollectionOrganizer.RequireCollection(catalog, collectionId);

    private static MediaSet[] FindSuggestionSets(CollectionCatalog catalog, string collectionId) =>
        catalog.Sets.Where(set => set.CollectionId == collectionId).ToArray();

    private static ImageRecord[] SelectSuggestionImages(List<ImageRecord> records, IEnumerable<string> mediaIds)
    {
        var ids = mediaIds.ToHashSet();
        return records.Where(image => ids.Contains(image.Id)).ToArray();
    }

    private static void EnsureSuggestionInputsAreUsable(ImageRecord[] images, MediaSet[] sets)
    {
        if (images.Length == 0 || sets.Length == 0)
            throw new InvalidOperationException("Choose media and a collection with existing sets first.");
        if (images.All(image => string.IsNullOrWhiteSpace(image.Description) && string.IsNullOrWhiteSpace(image.Tags)))
            throw new InvalidOperationException("Analyze or tag the selected media first so the model has content to use.");
    }

    private async Task<(List<ImageRecord> Records, VisionSettings Settings)> LoadSetSuggestionInputsAsync(
        UnlockedVaultSession generation, IGalleryStore store, CancellationToken cancellationToken)
    {
        await store.Gate.WaitAsync(cancellationToken);
        try
        {
            using var keyLease = generation.Borrow();
            var records = await store.ReadRecordsAsync(keyLease.Bytes);
            var settings = await store.ReadSettingsAsync<VisionSettings>(VisionSettingsId, VisionSettingsId, keyLease.Bytes) ?? new();
            return (records, settings);
        }
        finally { store.Gate.Release(); }
    }

    private static MediaSet? FindSuggestedSet(MediaSet[] sets, string suggestedId) =>
        sets.FirstOrDefault(set => set.Id == suggestedId);

    public async Task<VisionSettings> LoadSettingsAsync()
    {
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        return await galleryStore.ReadSettingsAsync<VisionSettings>(VisionSettingsId, VisionSettingsId, keyLease.Bytes) ?? new();
    }

    public async Task SaveSettingsAsync(VisionSettings settings)
    {
        settings = VisionSettingsValidator.ValidateAndNormalize(settings);
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        await galleryStore.SaveSettingsAsync(settings, VisionSettingsId, VisionSettingsId, keyLease.Bytes);
    }

    public async Task<VisionCheckpointSession> OpenVisionCheckpointAsync()
    {
        var generation = vaultSession.AcquireUnlockedSession();
        await checkpointSessionGate.WaitAsync(generation.RevocationToken);
        try
        {
            if (ReferenceEquals(checkpointGeneration, generation) && checkpointSession is not null) return checkpointSession;
            var store = galleryStore;
            var state = await ReadVisionCheckpointAsync(generation, store);
            generation.RevocationToken.ThrowIfCancellationRequested();
            checkpointGeneration = generation;
            checkpointSession = CreateVisionCheckpointSession(generation, store, state);
            return checkpointSession;
        }
        finally { checkpointSessionGate.Release(); }
    }

    private async Task<VisionCheckpoint> ReadVisionCheckpointAsync(UnlockedVaultSession generation, IGalleryStore store)
    {
        await store.Gate.WaitAsync(generation.RevocationToken);
        try
        {
            using var keyLease = generation.Borrow();
            return await store.ReadSettingsAsync<VisionCheckpoint>(VisionCheckpoint.SettingId, VisionCheckpoint.SettingPurpose, keyLease.Bytes) ?? new();
        }
        finally { store.Gate.Release(); }
    }

    private VisionCheckpointSession CreateVisionCheckpointSession(UnlockedVaultSession generation, IGalleryStore store, VisionCheckpoint state) =>
        new(state, generation.RevocationToken,
            checkpoint => SaveVisionCheckpointAsync(generation, store, checkpoint),
            (draft, checkpoint) => ApproveVisionAsync(generation, store, draft.Id, draft.Description, draft.Tags, draft.Model, draft.Analysis,
                draft.TagsOnly ? VisionApprovalMode.TagsOnly : VisionApprovalMode.Full,
                TagSources(draft.UseExistingTags, draft.SuggestNewTags), checkpoint));

    private static async Task SaveVisionCheckpointAsync(UnlockedVaultSession generation, IGalleryStore store, VisionCheckpoint checkpoint)
    {
        await store.Gate.WaitAsync(generation.RevocationToken);
        try
        {
            using var keyLease = generation.Borrow();
            await store.SaveSettingsAsync(checkpoint, VisionCheckpoint.SettingId, VisionCheckpoint.SettingPurpose, keyLease.Bytes);
        }
        finally { store.Gate.Release(); }
    }

    public async Task<VisionAnalysisResult> AnalyzeForDraftAsync(string id, VisionSettings settings, CancellationToken token = default)
    {
        var generation = vaultSession.AcquireUnlockedSession();
        var store = galleryStore;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, generation.RevocationToken);
        using var keyLease = generation.Borrow();
        var (source, names) = await LoadDraftAnalysisInputsAsync(id, store, keyLease.Bytes, cancellation.Token);
        cancellation.Token.ThrowIfCancellationRequested();
        var preview = await GalleryMediaReader.ReadPreviewAsync(store, source, keyLease.Bytes, ImageResolution.Fit);
        cancellation.Token.ThrowIfCancellationRequested();
        var result = await RequiredVisionManager().AnalyzeResultAsync(settings, preview, names, cancellation.Token);
        cancellation.Token.ThrowIfCancellationRequested();
        return EnrichDraftAnalysis(result, source, names, settings);
    }

    private static async Task<(ImageRecord Source, IReadOnlyList<string> Names)> LoadDraftAnalysisInputsAsync(
        string id, IGalleryStore store, byte[] key, CancellationToken cancellationToken)
    {
        var source = await store.ReadRecordAsync(id, key);
        await store.Gate.WaitAsync(cancellationToken);
        try { return (source, (await TagCatalogStore.ReadAsync(store, key)).Names); }
        finally { store.Gate.Release(); }
    }

    private static VisionAnalysisResult EnrichDraftAnalysis(VisionAnalysisResult result, ImageRecord source,
        IReadOnlyList<string> names, VisionSettings settings)
    {
        var tags = FilterVisionTags(result.Tags, names, TagSources(settings.UseExistingTags, settings.SuggestNewTags));
        var warning = tags == result.Tags
            ? result.Warning
            : (result.Warning + " Tag assignments were filtered/canonicalized using the selected tag settings.").Trim();
        var analysis = result.Analysis is null ? null : result.Analysis with
        {
            ExistingTagCandidates = settings.UseExistingTags ? names.ToImmutableArray() : [],
            ImageInput = source.IsVideo ? "video-thumbnail" : "fit-preview",
            SourceContentHash = source.ContentSha256,
            SourceDescription = source.Description,
            PreviousRunId = source.VisionFacts?.RunId ?? "",
            Facts = result.Analysis.Facts with { SourceContentHash = source.ContentSha256 }
        };
        return result with { Tags = tags, Warning = warning, Analysis = analysis };
    }

    private static VisionTagSources TagSources(bool useExistingTags, bool suggestNewTags) =>
        (useExistingTags ? VisionTagSources.Existing : VisionTagSources.None)
        | (suggestNewTags ? VisionTagSources.Suggested : VisionTagSources.None);

    private static string FilterVisionTags(string tags, IReadOnlyList<string> names, VisionTagSources tagSources)
    {
        var canonicalTagNamesByTagName = names.ToDictionary(name => name, StringComparer.OrdinalIgnoreCase);
        return string.Join(", ", tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tag => tag.Length <= 100 && !tag.Any(char.IsControl))
            .Where(tag => IsTagSourceAllowed(canonicalTagNamesByTagName, tag, tagSources))
            .Select(tag => canonicalTagNamesByTagName.GetValueOrDefault(tag, tag)).Distinct(StringComparer.OrdinalIgnoreCase).Take(100));
    }

    private static bool IsTagSourceAllowed(IReadOnlyDictionary<string, string> canonicalTagNamesByTagName,
        string tag, VisionTagSources tagSources) =>
        canonicalTagNamesByTagName.ContainsKey(tag)
            ? (tagSources & VisionTagSources.Existing) != 0
            : (tagSources & VisionTagSources.Suggested) != 0;

    public async Task<ImageRecord> ApproveVisionAsync(string id, string description, string tags, string model,
        VisionAnalysis? analysis, bool tagsOnly = false, bool useExistingTags = true, bool suggestNewTags = true)
    {
        var generation = vaultSession.AcquireUnlockedSession();
        return await ApproveVisionAsync(generation, galleryStore, id, description, tags, model, analysis,
            tagsOnly ? VisionApprovalMode.TagsOnly : VisionApprovalMode.Full, TagSources(useExistingTags, suggestNewTags), null);
    }

    private static async Task<ImageRecord> ApproveVisionAsync(UnlockedVaultSession generation, IGalleryStore store,
        string id, string description, string tags, string model, VisionAnalysis? analysis,
        VisionApprovalMode approvalMode, VisionTagSources tagSources, VisionCheckpoint? checkpoint)
    {
        using var keyLease = generation.Borrow();
        await store.Gate.WaitAsync(generation.RevocationToken);
        try
        {
            var record = await store.ReadRecordAsync(id, keyLease.Bytes);
            var catalog = await TagCatalogStore.ReadAsync(store, keyLease.Bytes);
            if (approvalMode == VisionApprovalMode.Full && analysis is not null && IsStaleAnalysis(record, analysis))
                throw new InvalidOperationException("This analysis is stale. The image, vision notes or accepted analysis changed. Analyze again before approval.");
            var updated = BuildApprovedRecord(record, description, tags, model, analysis, approvalMode, tagSources, catalog.Names);
            var nextCatalog = BuildApprovedTagCatalog(catalog, updated);
            generation.RevocationToken.ThrowIfCancellationRequested();
            await store.CommitVisionApprovalAsync(updated, nextCatalog,
                approvalMode == VisionApprovalMode.Full ? analysis : null, checkpoint, keyLease.Bytes);
            return updated;
        }
        finally { store.Gate.Release(); }
    }

    private static bool IsStaleAnalysis(ImageRecord record, VisionAnalysis analysis) =>
        record.ContentSha256 != analysis.SourceContentHash
        || record.Description != analysis.SourceDescription
        || (record.VisionFacts?.RunId ?? "") != analysis.PreviousRunId;

    private static ImageRecord BuildApprovedRecord(ImageRecord record, string description, string tags, string model,
        VisionAnalysis? analysis, VisionApprovalMode approvalMode, VisionTagSources tagSources, IReadOnlyList<string> tagNames)
    {
        var tagsOnly = approvalMode == VisionApprovalMode.TagsOnly;
        var eligibleTags = FilterVisionTags(tags, tagNames, tagSources);
        return record with
        {
            Description = tagsOnly || string.IsNullOrWhiteSpace(description) ? record.Description : description,
            Tags = LibraryRules.MergeTags(record.Tags, eligibleTags),
            AnalysisModel = tagsOnly ? record.AnalysisModel : model,
            VisionFacts = !tagsOnly && analysis is not null ? analysis.Facts : record.VisionFacts
        };
    }

    private static TagCatalog BuildApprovedTagCatalog(TagCatalog catalog, ImageRecord updated) =>
        new(TagCatalogStore.NormalizeTagNames(catalog.Names.Concat(TagCatalogStore.TagsOf(updated))));

    public async Task<VisionAnalysis?> ReadVisionAnalysisAsync(string id)
    {
        var generation = vaultSession.AcquireUnlockedSession();
        var store = galleryStore;
        using var keyLease = generation.Borrow();
        await store.Gate.WaitAsync(generation.RevocationToken);
        try
        {
            var record = await store.ReadRecordAsync(id, keyLease.Bytes);
            var analysis = await store.ReadVisionAnalysisAsync(id, keyLease.Bytes);
            generation.RevocationToken.ThrowIfCancellationRequested();
            return analysis is not null && MatchesAcceptedAnalysis(record, analysis) ? analysis : null;
        }
        finally { store.Gate.Release(); }
    }

    private static bool MatchesAcceptedAnalysis(ImageRecord record, VisionAnalysis analysis) =>
        analysis.SourceContentHash == record.ContentSha256 && analysis.RunId == record.VisionFacts?.RunId;

    public async Task<ExportFile> ExportVisionAnalysisAsync(string id)
    {
        var analysis = await ReadVisionAnalysisAsync(id) ?? throw new InvalidOperationException("No accepted structured analysis is available.");
        return new($"{id}.analysis.json", "application/json", Encoding.UTF8.GetBytes(analysis.Json));
    }

    private VisionManager RequiredVisionManager() => visionManager
        ?? throw new InvalidOperationException("The vision manager is not configured.");
}
