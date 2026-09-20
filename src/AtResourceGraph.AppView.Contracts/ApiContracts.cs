using System.Text.Json;
using System.Text.Json.Serialization;

namespace AtResourceGraph.AppView.Contracts;

/// <summary>Stable JSON options for the draft sample API.</summary>
public static class AppViewJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };
}

/// <summary>Application-specific envelope for an indexed bundle response.</summary>
public sealed record AppViewBundleResponse(
    string BundleIdentity,
    string Name,
    string? Description,
    AppViewSource Source,
    DateTimeOffset IndexedAt,
    int ProjectionVersion,
    IReadOnlyList<AppViewMember> Members,
    IReadOnlyList<AppViewDiagnostic> Diagnostics);

/// <summary>Source provenance retained by the projection.</summary>
public sealed record AppViewSource(string? Cid, string? Revision);

/// <summary>One ordered bundle member and its indexed projection.</summary>
public sealed record AppViewMember(
    int Position,
    string? Label,
    string Kind,
    string Identity,
    string? Uri,
    string? SourceCid,
    string? SourceRevision,
    AppViewProjectedResource? Projected);

/// <summary>Normalized item or feed metadata projected offline.</summary>
public sealed record AppViewProjectedResource(
    string Kind,
    string? Title,
    string? Text,
    string? Link,
    DateTimeOffset? Published,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>A persisted graph diagnostic exposed without changing primitive contracts.</summary>
public sealed record AppViewDiagnostic(
    string Code,
    string Message,
    string Severity,
    string? ResourceIdentity,
    int? Position);

/// <summary>Explicit error envelope for draft API failures.</summary>
public sealed record AppViewError(string Error, string Message);
