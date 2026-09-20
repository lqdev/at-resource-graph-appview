using System.Globalization;
using System.Text.Json;
using ResourceGraph.AtProto;
using ResourceGraph.Bluesky;
using ResourceGraph.Core;
using ResourceGraph.Syndication;

namespace AtResourceGraph.AppView.Application;

/// <summary>Imports checked-in fixture events into the deterministic projection.</summary>
public sealed class FixtureImporter
{
    private readonly ProjectionStore store;

    public FixtureImporter(ProjectionStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<IReadOnlyList<IngestionResult>> ImportDirectoryAsync(
        string fixtureDirectory,
        string eventFileName = "events.jsonl",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fixtureDirectory))
        {
            throw new ArgumentException("A fixture directory is required.", nameof(fixtureDirectory));
        }

        var root = Path.GetFullPath(fixtureDirectory);
        var assets = await FixtureAssetCatalog.LoadAsync(root, cancellationToken);
        var eventPath = Path.Combine(root, eventFileName);
        if (!File.Exists(eventPath))
        {
            throw new FileNotFoundException("The fixture event file was not found.", eventPath);
        }

        var results = new List<IngestionResult>();
        await using var stream = File.OpenRead(eventPath);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var ingestionEvent = ParseEvent(line);
            var projection = ingestionEvent.Operation == IngestionOperation.Upsert
                ? FixtureProjector.Project(ingestionEvent, assets)
                : null;
            results.Add(await store.ApplyAsync(ingestionEvent, projection, cancellationToken));
        }

        return results;
    }

    public static IngestionEvent ParseEvent(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var eventId = RequiredString(root, "eventId");
        var cursor = RequiredString(root, "cursor");
        var operation = RequiredString(root, "operation").ToLowerInvariant() switch
        {
            "upsert" => IngestionOperation.Upsert,
            "delete" => IngestionOperation.Delete,
            _ => throw new JsonException("Fixture operation must be 'upsert' or 'delete'.")
        };
        var indexedAtText = RequiredString(root, "indexedAt");
        if (!DateTimeOffset.TryParse(
                indexedAtText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var indexedAt))
        {
            throw new JsonException("Fixture indexedAt must be an ISO-8601 timestamp.");
        }

        var bundleElement = root.TryGetProperty("bundle", out var bundle)
            ? bundle.GetRawText()
            : null;
        var bundleIdentity = OptionalString(root, "bundleIdentity");
        if (operation == IngestionOperation.Upsert)
        {
            if (bundleElement is null)
            {
                throw new JsonException("An upsert fixture requires a bundle.");
            }

            bundleIdentity = BundleJson.Deserialize(bundleElement).Identity;
        }

        if (string.IsNullOrWhiteSpace(bundleIdentity))
        {
            throw new JsonException("A fixture requires bundleIdentity.");
        }

        return new IngestionEvent(
            eventId,
            cursor,
            operation,
            bundleIdentity,
            indexedAt,
            OptionalString(root, "sourceCid"),
            OptionalString(root, "sourceRevision"),
            bundleElement);
    }

    private static string RequiredString(JsonElement root, string name) =>
        OptionalString(root, name)
        ?? throw new JsonException($"Fixture property '{name}' must be a non-empty string.");

    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
}

internal sealed class FixtureAssetCatalog
{
    public required IReadOnlyDictionary<string, AtRecordEnvelope> Records { get; init; }
    public required IReadOnlyDictionary<string, SyndicationFeed> Feeds { get; init; }

    public static async Task<FixtureAssetCatalog> LoadAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var records = new Dictionary<string, AtRecordEnvelope>(StringComparer.Ordinal);
        var recordsDirectory = Path.Combine(root, "records");
        if (Directory.Exists(recordsDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(recordsDirectory, "*.json").OrderBy(path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var json = await File.ReadAllTextAsync(path, cancellationToken);
                var envelope = AtRecordEnvelopeDecoder.Decode(json);
                records[envelope.Uri.ToString()] = envelope;
            }
        }

        var feeds = new Dictionary<string, SyndicationFeed>(StringComparer.Ordinal);
        var feedsDirectory = Path.Combine(root, "feeds");
        if (Directory.Exists(feedsDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(feedsDirectory, "*.xml").OrderBy(path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var feed = SyndicationParser.Parse(await File.ReadAllTextAsync(path, cancellationToken));
                if (feed.Link is not null)
                {
                    feeds[feed.Link.AbsoluteUri] = feed;
                }
            }
        }

        return new FixtureAssetCatalog { Records = records, Feeds = feeds };
    }
}

internal static class FixtureProjector
{
    public static ProjectedBundle Project(IngestionEvent ingestionEvent, FixtureAssetCatalog assets)
    {
        if (ingestionEvent.BundleJson is null)
        {
            throw new InvalidDataException("An upsert fixture is missing bundle JSON.");
        }

        var bundle = BundleJson.Deserialize(ingestionEvent.BundleJson);
        var diagnostics = GraphValidator.Validate(bundle).ToList();
        var members = new List<ProjectedMember>();
        foreach (var membership in DeterministicGraph.OrderMemberships(bundle.Members))
        {
            switch (membership.Resource)
            {
                case AtRecordReference atRecord:
                    members.Add(ProjectAtRecord(membership, atRecord, assets, diagnostics, ingestionEvent));
                    break;
                case SyndicationReference syndication:
                    members.Add(ProjectSyndication(membership, syndication, assets, diagnostics));
                    break;
                default:
                    members.Add(new ProjectedMember(membership, null, null));
                    diagnostics.Add(new GraphDiagnostic(
                        "projection.resource.unsupported",
                        $"Resource kind '{membership.Resource.Kind}' is retained without a fixture projection.",
                        DiagnosticSeverity.Info,
                        membership.Resource.Identity,
                        membership.Position));
                    break;
            }
        }

        return new ProjectedBundle(bundle, members, diagnostics);
    }

    private static ProjectedMember ProjectAtRecord(
        Membership membership,
        AtRecordReference reference,
        FixtureAssetCatalog assets,
        List<GraphDiagnostic> diagnostics,
        IngestionEvent ingestionEvent)
    {
        if (!assets.Records.TryGetValue(reference.Identity, out var envelope))
        {
            diagnostics.Add(new GraphDiagnostic(
                "projection.record.missing",
                $"No checked-in AT record fixture was found for '{reference.Identity}'.",
                DiagnosticSeverity.Error,
                reference.Identity,
                membership.Position));
            return new ProjectedMember(membership, null, null);
        }

        if (reference.Cid is not null &&
            !string.Equals(reference.Cid, envelope.Cid, StringComparison.Ordinal))
        {
            diagnostics.Add(new GraphDiagnostic(
                "projection.record.cid_mismatch",
                $"The reference CID '{reference.Cid}' does not match fixture CID '{envelope.Cid}'.",
                DiagnosticSeverity.Error,
                reference.Identity,
                membership.Position));
        }

        var result = BlueskyAdapter.Project(envelope);
        diagnostics.AddRange(result.Diagnostics.Select(diagnostic => new GraphDiagnostic(
            diagnostic.Code,
            diagnostic.Message,
            diagnostic.Severity,
            diagnostic.ResourceIdentity,
            diagnostic.Position ?? membership.Position)));
        var projected = result.Value is null ? null : FromBluesky(result.Value, envelope);
        var sourceRecord = new ProjectedSourceRecord(
            envelope,
            ingestionEvent.SourceRevision ?? "fixture-revision-unknown",
            projected);
        return new ProjectedMember(membership, projected, sourceRecord);
    }

    private static ProjectedMember ProjectSyndication(
        Membership membership,
        SyndicationReference reference,
        FixtureAssetCatalog assets,
        List<GraphDiagnostic> diagnostics)
    {
        if (!assets.Feeds.TryGetValue(reference.Uri.AbsoluteUri, out var feed))
        {
            diagnostics.Add(new GraphDiagnostic(
                "projection.feed.missing",
                $"No checked-in syndication fixture was found for '{reference.Uri.AbsoluteUri}'.",
                DiagnosticSeverity.Warning,
                reference.Identity,
                membership.Position));
            return new ProjectedMember(membership, null, null);
        }

        var item = feed.Items.FirstOrDefault();
        var metadata = new Dictionary<string, string>(feed.Metadata, StringComparer.Ordinal)
        {
            ["format"] = feed.Format.ToString().ToLowerInvariant(),
            ["itemCount"] = feed.Items.Length.ToString(CultureInfo.InvariantCulture)
        };
        return new ProjectedMember(
            membership,
            new ProjectedResource(
                "syndication",
                feed.Title ?? reference.Title,
                item?.Summary,
                item?.Link?.AbsoluteUri ?? feed.Link?.AbsoluteUri,
                item?.Published?.ToUniversalTime(),
                metadata),
            null);
    }

    private static ProjectedResource FromBluesky(BlueskyItem item, AtRecordEnvelope envelope) =>
        new(
            item.Kind.ToString().ToLowerInvariant(),
            item.Title,
            item.Text,
            item.Link.AbsoluteUri,
            item.Published?.ToUniversalTime(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["authorDid"] = item.AuthorDid,
                ["recordType"] = envelope.RecordType ?? string.Empty,
                ["cid"] = envelope.Cid
            });
}
