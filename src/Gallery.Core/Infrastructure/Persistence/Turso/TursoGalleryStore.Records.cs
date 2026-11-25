using System.Collections.Immutable;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Gallery.Core.Models;

namespace Gallery.Core.Infrastructure.Persistence.Turso;

internal sealed partial class TursoGalleryStore
{
    private const int CollectionMembershipBatchSize = 500;

    private enum RecordWriteMode
    {
        Insert,
        Update
    }

    private sealed record MediaRecordRow
    {
        public required string Id { get; init; }
        public string? SetId { get; init; }
        public long SetOrder { get; init; }
        public ImmutableArray<string> LegacyCollections { get; init; }
        public required string Name { get; init; }
        public required string OriginalName { get; init; }
        public required string StoredPath { get; init; }
        public required string SourceFolder { get; init; }
        public required string SourcePath { get; init; }
        public DateTimeOffset? SourceCreatedAt { get; init; }
        public DateTimeOffset ImportedAt { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public long OriginalBytes { get; init; }
        public required string Sha256 { get; init; }
        public required string ContentSha256 { get; init; }
        public required string Description { get; init; }
        public required string UserDescription { get; init; }
        public required string Tags { get; init; }
        public required string AnalysisModel { get; init; }
        public VisionFacts? VisionFacts { get; init; }
        public bool IsFavorite { get; init; }
        public bool IsMarkedForDeletion { get; init; }
        public int Rating { get; init; }
        public bool IsReviewed { get; init; }
        public required string MediaType { get; init; }
        public required string ContentType { get; init; }
        public double DurationSeconds { get; init; }
        public required string Metadata { get; init; }
        public int MetadataVersion { get; init; }
        public GenerationInfo? Generation { get; init; }
    }

    private sealed record MediaColumn(string Name, Func<ImageRecord, object?> Value);
    private static readonly MediaColumn[] MediaColumns =
    [
        new("set_id", r => r.SetId),
        new("set_order", r => r.SetOrder),
        new("legacy_collections", r => JsonSerializer.Serialize(r.LegacyCollections)),
        new("name", r => r.Name),
        new("original_name", r => r.OriginalName),
        new("stored_path", r => r.StoredPath),
        new("source_folder", r => r.SourceFolder),
        new("source_path", r => r.SourcePath),
        new("source_created_at", r => r.SourceCreatedAt?.ToString("O", CultureInfo.InvariantCulture)),
        new("imported_at", r => r.ImportedAt.ToString("O", CultureInfo.InvariantCulture)),
        new("width", r => r.Width),
        new("height", r => r.Height),
        new("original_bytes", r => r.OriginalBytes),
        new("source_sha256", r => r.Sha256),
        new("content_sha256", r => r.ContentSha256),
        new("description", r => r.Description),
        new("user_description", r => r.UserDescription),
        new("tags", r => r.Tags),
        new("analysis_model", r => r.AnalysisModel),
        new("vision_facts", r => r.VisionFacts is null ? null : JsonSerializer.Serialize(r.VisionFacts)),
        new("is_favorite", r => r.IsFavorite ? 1L : 0L),
        new("is_marked_for_deletion", r => r.IsMarkedForDeletion ? 1L : 0L),
        new("rating", r => r.Rating),
        new("is_reviewed", r => r.IsReviewed ? 1L : 0L),
        new("media_type", r => r.MediaType),
        new("content_type", r => r.ContentType),
        new("duration_seconds", r => r.DurationSeconds),
        new("imported_ticks", r => r.ImportedAt.UtcTicks),
        new("source_identity", r => NormalizeSourceHash(r.Sha256))
    ];

    private static readonly string SelectRecordSql =
        "SELECT m.id," + string.Join(',', MediaColumns.Select(c => "m." + c.Name)) +
        ",d.document,d.parser_version,g.media_id AS generation_id," +
        "g.prompt,g.negative_prompt,g.cfg,g.steps,g.model,g.model_kind,g.sampler,g.scheduler,g.has_workflow,g.has_api_prompt " +
        "FROM media m JOIN media_metadata d ON d.media_id=m.id LEFT JOIN media_generation g ON g.media_id=m.id ";

    private static async Task<List<ImageRecord>> ReadRecordsAsync(DbConnection connection, string suffix,
        CancellationToken token, DbTransaction? transaction = null, params (string Name, object? Value)[] parameters)
    {
        var recordRows = new List<MediaRecordRow>();
        using (var command = Command(connection, SelectRecordSql + suffix, transaction, parameters))
        {
            using var cancellation = token.Register(command.Cancel);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                token.ThrowIfCancellationRequested();
                recordRows.Add(MapRecord(reader));
            }
        }
        // Keep collection membership relational, without one query per item or unbounded IN lists.
        var records = new List<ImageRecord>(recordRows.Count);
        for (var offset = 0; offset < recordRows.Count; offset += CollectionMembershipBatchSize)
        {
            token.ThrowIfCancellationRequested();
            var batch = recordRows.Skip(offset).Take(CollectionMembershipBatchSize).ToArray();
            var batchIndicesByRecordId = batch.Select((recordRow, index) => (recordRow.Id, index))
                .ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
            var memberships = batch.Select(_ => ImmutableArray.CreateBuilder<string>()).ToArray();
            using var command = Command(connection, "SELECT media_id,name FROM media_collections WHERE media_id IN (" +
                string.Join(',', batch.Select((_, i) => "$id" + i)) + ") ORDER BY ordinal", transaction,
                batch.Select((recordRow, index) => ("$id" + index, (object?)recordRow.Id)).ToArray());
            using var cancellation = token.Register(command.Cancel);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                memberships[batchIndicesByRecordId[reader.GetString(0)]].Add(reader.GetString(1));
            for (var index = 0; index < batch.Length; index++)
                records.Add(ComposeRecord(batch[index], memberships[index].ToImmutable()));
        }
        return records;
    }

    private static MediaRecordRow MapRecord(DbDataReader reader)
    {
        string Text(string name) => reader.GetString(reader.GetOrdinal(name));
        long Number(string name) => reader.GetInt64(reader.GetOrdinal(name));
        double Real(string name) => reader.GetDouble(reader.GetOrdinal(name));
        bool Missing(string name) => reader.IsDBNull(reader.GetOrdinal(name));
        return new MediaRecordRow
        {
            Id = Text("id"),
            SetId = Missing("set_id") ? null : Text("set_id"),
            SetOrder = Number("set_order"),
            LegacyCollections = JsonSerializer.Deserialize<ImmutableArray<string>>(Text("legacy_collections")),
            Name = Text("name"),
            OriginalName = Text("original_name"),
            StoredPath = Text("stored_path"),
            SourceFolder = Text("source_folder"),
            SourcePath = Text("source_path"),
            SourceCreatedAt = Missing("source_created_at") ? null : DateTimeOffset.ParseExact(Text("source_created_at"), "O", CultureInfo.InvariantCulture),
            ImportedAt = DateTimeOffset.ParseExact(Text("imported_at"), "O", CultureInfo.InvariantCulture),
            Width = checked((int)Number("width")),
            Height = checked((int)Number("height")),
            OriginalBytes = Number("original_bytes"),
            Sha256 = Text("source_sha256"),
            ContentSha256 = Text("content_sha256"),
            Description = Text("description"),
            UserDescription = Text("user_description"),
            Tags = Text("tags"),
            AnalysisModel = Text("analysis_model"),
            VisionFacts = Missing("vision_facts") ? null : JsonSerializer.Deserialize<VisionFacts>(Text("vision_facts")),
            IsFavorite = Number("is_favorite") != 0,
            IsMarkedForDeletion = Number("is_marked_for_deletion") != 0,
            Rating = checked((int)Number("rating")),
            IsReviewed = Number("is_reviewed") != 0,
            MediaType = Text("media_type"),
            ContentType = Text("content_type"),
            DurationSeconds = Real("duration_seconds"),
            Metadata = Text("document"),
            MetadataVersion = checked((int)Number("parser_version")),
            Generation = Missing("generation_id") ? null : new GenerationInfo
            {
                Prompt = Text("prompt"),
                NegativePrompt = Text("negative_prompt"),
                Cfg = Missing("cfg") ? null : Real("cfg"),
                Steps = Missing("steps") ? null : checked((int)Number("steps")),
                Model = Text("model"),
                ModelKind = Text("model_kind"),
                Sampler = Text("sampler"),
                Scheduler = Text("scheduler"),
                HasWorkflow = Number("has_workflow") != 0,
                HasApiPrompt = Number("has_api_prompt") != 0,
            }
        };
    }

    private static ImageRecord ComposeRecord(MediaRecordRow row, ImmutableArray<string> collections) => new()
    {
        Id = row.Id,
        SetId = row.SetId,
        SetOrder = row.SetOrder,
        LegacyCollections = row.LegacyCollections,
        Name = row.Name,
        OriginalName = row.OriginalName,
        StoredPath = row.StoredPath,
        SourceFolder = row.SourceFolder,
        SourcePath = row.SourcePath,
        SourceCreatedAt = row.SourceCreatedAt,
        ImportedAt = row.ImportedAt,
        Width = row.Width,
        Height = row.Height,
        OriginalBytes = row.OriginalBytes,
        Sha256 = row.Sha256,
        ContentSha256 = row.ContentSha256,
        Description = row.Description,
        UserDescription = row.UserDescription,
        Tags = row.Tags,
        AnalysisModel = row.AnalysisModel,
        VisionFacts = row.VisionFacts,
        IsFavorite = row.IsFavorite,
        IsMarkedForDeletion = row.IsMarkedForDeletion,
        Collections = collections,
        Rating = row.Rating,
        IsReviewed = row.IsReviewed,
        MediaType = row.MediaType,
        ContentType = row.ContentType,
        DurationSeconds = row.DurationSeconds,
        Metadata = row.Metadata,
        MetadataVersion = row.MetadataVersion,
        Generation = row.Generation
    };

    private static async Task WriteRecordAsync(DbConnection connection, DbTransaction transaction,
        ImageRecord record, RecordWriteMode writeMode)
    {
        await WriteMediaRowAsync(connection, transaction, record, writeMode);
        await UpsertMediaMetadataAsync(connection, transaction, record);
        await ReplaceCollectionsAsync(connection, transaction, record);
        await ReplaceGenerationAsync(connection, transaction, record);
    }

    private static async Task WriteMediaRowAsync(DbConnection connection, DbTransaction transaction,
        ImageRecord record, RecordWriteMode writeMode)
    {
        var parameters = MediaColumns.Select(c => ("$" + c.Name, c.Value(record))).Prepend(("$id", (object?)record.Id)).ToArray();
        var sql = writeMode == RecordWriteMode.Insert
            ? "INSERT INTO media(id," + string.Join(',', MediaColumns.Select(c => c.Name)) + ") VALUES($id," +
                string.Join(',', MediaColumns.Select(c => "$" + c.Name)) + ")"
            : "UPDATE media SET " + string.Join(',', MediaColumns.Select(c => c.Name + "=$" + c.Name)) + " WHERE id=$id";
        RequireOne(await NonQueryAsync(connection, sql, transaction, parameters), record.Id);
    }

    private static Task UpsertMediaMetadataAsync(DbConnection connection, DbTransaction transaction, ImageRecord record) =>
        NonQueryAsync(connection, """
            INSERT INTO media_metadata(media_id,document,parser_version) VALUES($id,$document,$version)
            ON CONFLICT(media_id) DO UPDATE SET document=excluded.document,parser_version=excluded.parser_version
            WHERE media_metadata.document IS NOT excluded.document OR media_metadata.parser_version<>excluded.parser_version
            """, transaction, ("$id", record.Id), ("$document", record.Metadata), ("$version", record.MetadataVersion));

    private static async Task ReplaceCollectionsAsync(DbConnection connection, DbTransaction transaction, ImageRecord record)
    {
        await NonQueryAsync(connection, "DELETE FROM media_collections WHERE media_id=$id", transaction, ("$id", record.Id));
        for (var index = 0; index < record.Collections.Length; index++)
            await NonQueryAsync(connection, "INSERT INTO media_collections VALUES($id,$ordinal,$name)", transaction,
                ("$id", record.Id), ("$ordinal", index), ("$name", record.Collections[index]));
    }

    private static async Task ReplaceGenerationAsync(DbConnection connection, DbTransaction transaction, ImageRecord record)
    {
        await NonQueryAsync(connection, "DELETE FROM media_generation WHERE media_id=$id", transaction, ("$id", record.Id));
        if (record.Generation is not { } generation) return;
        await NonQueryAsync(connection,
            "INSERT INTO media_generation(media_id,prompt,negative_prompt,cfg,steps,model,model_kind,sampler,scheduler,has_workflow,has_api_prompt) VALUES($id,$prompt,$negative_prompt,$cfg,$steps,$model,$model_kind,$sampler,$scheduler,$has_workflow,$has_api_prompt)",
            transaction, ("$id", record.Id),
            ("$prompt", generation.Prompt),
            ("$negative_prompt", generation.NegativePrompt),
            ("$cfg", generation.Cfg),
            ("$steps", generation.Steps),
            ("$model", generation.Model),
            ("$model_kind", generation.ModelKind),
            ("$sampler", generation.Sampler),
            ("$scheduler", generation.Scheduler),
            ("$has_workflow", generation.HasWorkflow ? 1L : 0L),
            ("$has_api_prompt", generation.HasApiPrompt ? 1L : 0L));
    }
}
