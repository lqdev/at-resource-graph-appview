using System.Text.Json;
using ResourceGraph.AtProto;
using ResourceGraph.Core;

namespace AtResourceGraph.AppView.Application;

public enum IngestionOperation
{
    Upsert,
    Delete
}

public sealed record IngestionEvent(
    string EventId,
    string Cursor,
    IngestionOperation Operation,
    string BundleIdentity,
    DateTimeOffset IndexedAt,
    string? SourceCid,
    string? SourceRevision,
    string? BundleJson);

public sealed record ProjectedResource(
    string Kind,
    string? Title,
    string? Text,
    string? Link,
    DateTimeOffset? Published,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record ProjectedSourceRecord(
    AtRecordEnvelope Envelope,
    string Revision,
    ProjectedResource? Projection);

public sealed record ProjectedMember(
    Membership Membership,
    ProjectedResource? Projection,
    ProjectedSourceRecord? SourceRecord);

public sealed record ProjectedBundle(
    Bundle Bundle,
    IReadOnlyList<ProjectedMember> Members,
    IReadOnlyList<GraphDiagnostic> Diagnostics);

public sealed record IngestionResult(
    string EventId,
    string Cursor,
    bool Applied,
    bool Duplicate,
    string? RejectionCode = null);

public sealed record StoredBundleProjection(
    string Identity,
    string Name,
    string? Description,
    string? SelfUri,
    string? SourceCid,
    string? SourceRevision,
    int ProjectionVersion,
    DateTimeOffset IndexedAt,
    IReadOnlyList<StoredMember> Members,
    IReadOnlyList<GraphDiagnostic> Diagnostics);

public sealed record StoredMember(
    int Position,
    string? Label,
    string Kind,
    string Identity,
    string? Uri,
    string? Format,
    string? Cid,
    string? Title,
    string? SourceCid,
    string? SourceRevision,
    ProjectedResource? Projection);

internal static class SqliteValue
{
    public static void Add(Microsoft.Data.Sqlite.SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    public static string SerializeMetadata(IReadOnlyDictionary<string, string> metadata) =>
        JsonSerializer.Serialize(metadata, JsonSerializerOptions.Default);

    public static IReadOnlyDictionary<string, string> DeserializeMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
