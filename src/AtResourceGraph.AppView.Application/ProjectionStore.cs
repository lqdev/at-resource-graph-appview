using System.Globalization;
using Microsoft.Data.Sqlite;
using ResourceGraph.Core;

namespace AtResourceGraph.AppView.Application;

/// <summary>Deterministic SQLite schema and repository for the indexed read model.</summary>
public sealed class ProjectionStore : IDisposable
{
    public const int CurrentProjectionVersion = 1;
    private readonly string connectionString;
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private bool initialized;

    public ProjectionStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("A SQLite connection string is required.", nameof(connectionString));
        }

        this.connectionString = connectionString;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (initialized)
        {
            return;
        }

        await initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (initialized)
            {
                return;
            }

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                PRAGMA foreign_keys = ON;
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER PRIMARY KEY
                );
                CREATE TABLE IF NOT EXISTS bundles (
                    bundle_identity TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    description TEXT NULL,
                    self_uri TEXT NULL,
                    source_cid TEXT NULL,
                    source_revision TEXT NULL,
                    projection_version INTEGER NOT NULL,
                    indexed_at TEXT NOT NULL,
                    deleted_at TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS bundle_members (
                    bundle_identity TEXT NOT NULL,
                    position INTEGER NOT NULL,
                    label TEXT NULL,
                    resource_kind TEXT NOT NULL,
                    resource_identity TEXT NOT NULL,
                    resource_uri TEXT NULL,
                    resource_format TEXT NULL,
                    resource_cid TEXT NULL,
                    resource_title TEXT NULL,
                    source_record_identity TEXT NULL,
                    projected_kind TEXT NULL,
                    projected_title TEXT NULL,
                    projected_text TEXT NULL,
                    projected_link TEXT NULL,
                    projected_published TEXT NULL,
                    projected_metadata_json TEXT NULL,
                    PRIMARY KEY (bundle_identity, position),
                    FOREIGN KEY (bundle_identity) REFERENCES bundles(bundle_identity) ON DELETE CASCADE
                );
                CREATE TABLE IF NOT EXISTS source_records (
                    record_identity TEXT PRIMARY KEY,
                    at_uri TEXT NOT NULL,
                    cid TEXT NOT NULL,
                    revision TEXT NOT NULL,
                    record_json TEXT NOT NULL,
                    projected_kind TEXT NULL,
                    projected_title TEXT NULL,
                    projected_text TEXT NULL,
                    projected_link TEXT NULL,
                    projected_published TEXT NULL,
                    projected_metadata_json TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS projection_diagnostics (
                    diagnostic_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    bundle_identity TEXT NOT NULL,
                    code TEXT NOT NULL,
                    message TEXT NOT NULL,
                    severity TEXT NOT NULL,
                    resource_identity TEXT NULL,
                    position INTEGER NULL,
                    FOREIGN KEY (bundle_identity) REFERENCES bundles(bundle_identity) ON DELETE CASCADE
                );
                CREATE TABLE IF NOT EXISTS ingestion_cursor (
                    stream_name TEXT PRIMARY KEY,
                    last_cursor TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS ingestion_events (
                    event_id TEXT PRIMARY KEY,
                    stream_name TEXT NOT NULL,
                    cursor TEXT NOT NULL,
                    applied_at TEXT NOT NULL
                );
                INSERT OR IGNORE INTO schema_migrations(version) VALUES (1);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            initialized = true;
        }
        finally
        {
            initializationGate.Release();
        }
    }

    public async Task<bool> CanReadAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version = 1;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    public async Task<IngestionResult> ApplyAsync(
        IngestionEvent ingestionEvent,
        ProjectedBundle? projection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ingestionEvent);
        await InitializeAsync(cancellationToken);

        if (!long.TryParse(ingestionEvent.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var cursor))
        {
            throw new InvalidDataException($"Cursor '{ingestionEvent.Cursor}' is not a non-negative integer.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        await using (var duplicateCommand = connection.CreateCommand())
        {
            duplicateCommand.Transaction = transaction;
            duplicateCommand.CommandText =
                "SELECT 1 FROM ingestion_events WHERE event_id = $eventId LIMIT 1;";
            SqliteValue.Add(duplicateCommand, "$eventId", ingestionEvent.EventId);
            if (await duplicateCommand.ExecuteScalarAsync(cancellationToken) is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new IngestionResult(ingestionEvent.EventId, ingestionEvent.Cursor, false, true);
            }
        }

        var lastCursor = await ReadCursorAsync(connection, transaction, cancellationToken);
        if (lastCursor is not null &&
            long.TryParse(lastCursor, NumberStyles.None, CultureInfo.InvariantCulture, out var last) &&
            cursor <= last)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new IngestionResult(
                ingestionEvent.EventId,
                ingestionEvent.Cursor,
                false,
                false,
                "ingestion.cursor.out_of_order");
        }

        if (ingestionEvent.Operation == IngestionOperation.Upsert)
        {
            if (projection is null)
            {
                throw new ArgumentException("An upsert requires a projected bundle.", nameof(projection));
            }

            await UpsertBundleAsync(connection, transaction, ingestionEvent, projection, cancellationToken);
        }
        else
        {
            await DeleteBundleAsync(connection, transaction, ingestionEvent, cancellationToken);
        }

        await using (var eventCommand = connection.CreateCommand())
        {
            eventCommand.Transaction = transaction;
            eventCommand.CommandText =
                """
                INSERT INTO ingestion_events(event_id, stream_name, cursor, applied_at)
                VALUES ($eventId, 'fixtures', $cursor, $appliedAt);
                INSERT INTO ingestion_cursor(stream_name, last_cursor, updated_at)
                VALUES ('fixtures', $cursor, $updatedAt)
                ON CONFLICT(stream_name) DO UPDATE SET
                    last_cursor = excluded.last_cursor,
                    updated_at = excluded.updated_at;
                """;
            SqliteValue.Add(eventCommand, "$eventId", ingestionEvent.EventId);
            SqliteValue.Add(eventCommand, "$cursor", ingestionEvent.Cursor);
            SqliteValue.Add(eventCommand, "$appliedAt", ingestionEvent.IndexedAt.ToString("O", CultureInfo.InvariantCulture));
            SqliteValue.Add(eventCommand, "$updatedAt", ingestionEvent.IndexedAt.ToString("O", CultureInfo.InvariantCulture));
            await eventCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new IngestionResult(ingestionEvent.EventId, ingestionEvent.Cursor, true, false);
    }

    public async Task<StoredBundleProjection?> ReadBundleAsync(
        string bundleIdentity,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bundleIdentity))
        {
            throw new ArgumentException("A bundle identity is required.", nameof(bundleIdentity));
        }

        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var bundleCommand = connection.CreateCommand();
        bundleCommand.CommandText =
            """
            SELECT bundle_identity, name, description, self_uri, source_cid, source_revision,
                   projection_version, indexed_at
            FROM bundles
            WHERE bundle_identity = $identity AND deleted_at IS NULL;
            """;
        SqliteValue.Add(bundleCommand, "$identity", bundleIdentity);
        await using var reader = await bundleCommand.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var projection = new StoredBundleProjection(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetInt32(6),
            ParseTimestamp(reader.GetString(7)),
            Array.Empty<StoredMember>(),
            Array.Empty<GraphDiagnostic>());
        await reader.DisposeAsync();

        var members = await ReadMembersAsync(connection, bundleIdentity, cancellationToken);
        var diagnostics = await ReadDiagnosticsAsync(connection, bundleIdentity, cancellationToken);
        return projection with { Members = members, Diagnostics = diagnostics };
    }

    private static async Task UpsertBundleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IngestionEvent ingestionEvent,
        ProjectedBundle projection,
        CancellationToken cancellationToken)
    {
        await using (var bundleCommand = connection.CreateCommand())
        {
            bundleCommand.Transaction = transaction;
            bundleCommand.CommandText =
                """
                INSERT INTO bundles(
                    bundle_identity, name, description, self_uri, source_cid, source_revision,
                    projection_version, indexed_at, deleted_at)
                VALUES ($identity, $name, $description, $selfUri, $sourceCid, $sourceRevision,
                        $projectionVersion, $indexedAt, NULL)
                ON CONFLICT(bundle_identity) DO UPDATE SET
                    name = excluded.name,
                    description = excluded.description,
                    self_uri = excluded.self_uri,
                    source_cid = excluded.source_cid,
                    source_revision = excluded.source_revision,
                    projection_version = excluded.projection_version,
                    indexed_at = excluded.indexed_at,
                    deleted_at = NULL;
                DELETE FROM bundle_members WHERE bundle_identity = $identity;
                DELETE FROM projection_diagnostics WHERE bundle_identity = $identity;
                """;
            SqliteValue.Add(bundleCommand, "$identity", projection.Bundle.Identity);
            SqliteValue.Add(bundleCommand, "$name", projection.Bundle.Name);
            SqliteValue.Add(bundleCommand, "$description", projection.Bundle.Description);
            SqliteValue.Add(bundleCommand, "$selfUri", projection.Bundle.SelfUriText);
            SqliteValue.Add(bundleCommand, "$sourceCid", ingestionEvent.SourceCid);
            SqliteValue.Add(bundleCommand, "$sourceRevision", ingestionEvent.SourceRevision);
            SqliteValue.Add(bundleCommand, "$projectionVersion", CurrentProjectionVersion);
            SqliteValue.Add(bundleCommand, "$indexedAt", ingestionEvent.IndexedAt.ToString("O", CultureInfo.InvariantCulture));
            await bundleCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var member in projection.Members.OrderBy(item => item.Membership.Position))
        {
            await using var memberCommand = connection.CreateCommand();
            memberCommand.Transaction = transaction;
            memberCommand.CommandText =
                """
                INSERT INTO bundle_members(
                    bundle_identity, position, label, resource_kind, resource_identity, resource_uri,
                    resource_format, resource_cid, resource_title, source_record_identity,
                    projected_kind, projected_title, projected_text, projected_link,
                    projected_published, projected_metadata_json)
                VALUES ($bundleIdentity, $position, $label, $kind, $identity, $uri, $format, $cid,
                        $title, $sourceRecordIdentity, $projectedKind, $projectedTitle, $projectedText,
                        $projectedLink, $projectedPublished, $projectedMetadata);
                """;
            var resource = member.Membership.Resource;
            SqliteValue.Add(memberCommand, "$bundleIdentity", projection.Bundle.Identity);
            SqliteValue.Add(memberCommand, "$position", member.Membership.Position);
            SqliteValue.Add(memberCommand, "$label", member.Membership.Label);
            SqliteValue.Add(memberCommand, "$kind", resource.Kind.ToString());
            SqliteValue.Add(memberCommand, "$identity", resource.Identity);
            SqliteValue.Add(memberCommand, "$uri", ResourceText(resource));
            SqliteValue.Add(memberCommand, "$format", resource is SyndicationReference syndication
                ? syndication.Format.ToString()
                : null);
            SqliteValue.Add(memberCommand, "$cid", resource is AtRecordReference atRecord ? atRecord.Cid : null);
            SqliteValue.Add(memberCommand, "$title", resource.Title);
            SqliteValue.Add(memberCommand, "$sourceRecordIdentity", member.SourceRecord?.Envelope.Uri.ToString());
            AddProjectionParameters(memberCommand, member.Projection);
            await memberCommand.ExecuteNonQueryAsync(cancellationToken);

            if (member.SourceRecord is not null)
            {
                await UpsertSourceRecordAsync(connection, transaction, member.SourceRecord, cancellationToken);
            }
        }

        foreach (var diagnostic in projection.Diagnostics)
        {
            await using var diagnosticCommand = connection.CreateCommand();
            diagnosticCommand.Transaction = transaction;
            diagnosticCommand.CommandText =
                """
                INSERT INTO projection_diagnostics(
                    bundle_identity, code, message, severity, resource_identity, position)
                VALUES ($bundleIdentity, $code, $message, $severity, $resourceIdentity, $position);
                """;
            SqliteValue.Add(diagnosticCommand, "$bundleIdentity", projection.Bundle.Identity);
            SqliteValue.Add(diagnosticCommand, "$code", diagnostic.Code);
            SqliteValue.Add(diagnosticCommand, "$message", diagnostic.Message);
            SqliteValue.Add(diagnosticCommand, "$severity", diagnostic.Severity.ToString());
            SqliteValue.Add(diagnosticCommand, "$resourceIdentity", diagnostic.ResourceIdentity);
            SqliteValue.Add(diagnosticCommand, "$position", diagnostic.Position);
            await diagnosticCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task DeleteBundleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IngestionEvent ingestionEvent,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE bundles
            SET deleted_at = $deletedAt, indexed_at = $indexedAt, source_cid = $sourceCid,
                source_revision = $sourceRevision
            WHERE bundle_identity = $identity;
            """;
        SqliteValue.Add(command, "$deletedAt", ingestionEvent.IndexedAt.ToString("O", CultureInfo.InvariantCulture));
        SqliteValue.Add(command, "$indexedAt", ingestionEvent.IndexedAt.ToString("O", CultureInfo.InvariantCulture));
        SqliteValue.Add(command, "$sourceCid", ingestionEvent.SourceCid);
        SqliteValue.Add(command, "$sourceRevision", ingestionEvent.SourceRevision);
        SqliteValue.Add(command, "$identity", ingestionEvent.BundleIdentity);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertSourceRecordAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProjectedSourceRecord sourceRecord,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO source_records(
                record_identity, at_uri, cid, revision, record_json, projected_kind,
                projected_title, projected_text, projected_link, projected_published,
                projected_metadata_json)
            VALUES ($identity, $atUri, $cid, $revision, $recordJson, $projectedKind,
                    $projectedTitle, $projectedText, $projectedLink, $projectedPublished,
                    $projectedMetadata)
            ON CONFLICT(record_identity) DO UPDATE SET
                at_uri = excluded.at_uri,
                cid = excluded.cid,
                revision = excluded.revision,
                record_json = excluded.record_json,
                projected_kind = excluded.projected_kind,
                projected_title = excluded.projected_title,
                projected_text = excluded.projected_text,
                projected_link = excluded.projected_link,
                projected_published = excluded.projected_published,
                projected_metadata_json = excluded.projected_metadata_json;
            """;
        var envelope = sourceRecord.Envelope;
        SqliteValue.Add(command, "$identity", envelope.Uri.ToString());
        SqliteValue.Add(command, "$atUri", envelope.Uri.ToString());
        SqliteValue.Add(command, "$cid", envelope.Cid);
        SqliteValue.Add(command, "$revision", sourceRecord.Revision);
        SqliteValue.Add(command, "$recordJson", envelope.Value.GetRawText());
        AddProjectionParameters(command, sourceRecord.Projection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddProjectionParameters(SqliteCommand command, ProjectedResource? projection)
    {
        SqliteValue.Add(command, "$projectedKind", projection?.Kind);
        SqliteValue.Add(command, "$projectedTitle", projection?.Title);
        SqliteValue.Add(command, "$projectedText", projection?.Text);
        SqliteValue.Add(command, "$projectedLink", projection?.Link);
        SqliteValue.Add(
            command,
            "$projectedPublished",
            projection?.Published?.ToString("O", CultureInfo.InvariantCulture));
        SqliteValue.Add(
            command,
            "$projectedMetadata",
            projection is null ? null : SqliteValue.SerializeMetadata(projection.Metadata));
    }

    private static string? ResourceText(ResourceReference resource) =>
        resource switch
        {
            AtRecordReference atRecord => atRecord.AtUri,
            NestedBundleReference nested => nested.BundleUri,
            _ => resource.Uri?.AbsoluteUri
        };

    private static async Task<string?> ReadCursorAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT last_cursor FROM ingestion_cursor WHERE stream_name = 'fixtures';";
        return (await command.ExecuteScalarAsync(cancellationToken)) as string;
    }

    private static async Task<IReadOnlyList<StoredMember>> ReadMembersAsync(
        SqliteConnection connection,
        string bundleIdentity,
        CancellationToken cancellationToken)
    {
        var members = new List<StoredMember>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT position, label, resource_kind, resource_identity, resource_uri, resource_format,
                   resource_cid, resource_title, projected_kind, projected_title, projected_text,
                   projected_link, projected_published, projected_metadata_json
            FROM bundle_members
            WHERE bundle_identity = $identity
            ORDER BY position;
            """;
        SqliteValue.Add(command, "$identity", bundleIdentity);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            members.Add(new StoredMember(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                ReadProjection(reader, 8)));
        }

        return members;
    }

    private static async Task<IReadOnlyList<GraphDiagnostic>> ReadDiagnosticsAsync(
        SqliteConnection connection,
        string bundleIdentity,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<GraphDiagnostic>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT code, message, severity, resource_identity, position
            FROM projection_diagnostics
            WHERE bundle_identity = $identity
            ORDER BY diagnostic_id;
            """;
        SqliteValue.Add(command, "$identity", bundleIdentity);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            diagnostics.Add(new GraphDiagnostic(
                reader.GetString(0),
                reader.GetString(1),
                Enum.Parse<DiagnosticSeverity>(reader.GetString(2), ignoreCase: false),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4)));
        }

        return diagnostics;
    }

    private static ProjectedResource? ReadProjection(SqliteDataReader reader, int startIndex)
    {
        if (reader.IsDBNull(startIndex))
        {
            return null;
        }

        return new ProjectedResource(
            reader.GetString(startIndex),
            reader.IsDBNull(startIndex + 1) ? null : reader.GetString(startIndex + 1),
            reader.IsDBNull(startIndex + 2) ? null : reader.GetString(startIndex + 2),
            reader.IsDBNull(startIndex + 3) ? null : reader.GetString(startIndex + 3),
            reader.IsDBNull(startIndex + 4) ? null : ParseTimestamp(reader.GetString(startIndex + 4)),
            SqliteValue.DeserializeMetadata(reader.IsDBNull(startIndex + 5) ? null : reader.GetString(startIndex + 5)));
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public void Dispose() => initializationGate.Dispose();
}
