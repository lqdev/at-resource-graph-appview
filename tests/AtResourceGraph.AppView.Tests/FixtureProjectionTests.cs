using System.Net;
using System.Net.Http.Json;
using AtResourceGraph.AppView.Application;
using AtResourceGraph.AppView.Contracts;
using AtResourceGraph.Reader;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AtResourceGraph.AppView.Tests;

public sealed class FixtureProjectionTests
{
    private const string BundleIdentity =
        "at://did:plc:fixture/me.lqdev.resourcegraph.bundle/reading-list";

    [Fact]
    public async Task FixtureProjectionIsOrderedAndPreservesSourceMetadata()
    {
        var databasePath = NewDatabasePath();
        try
        {
            using (var store = new ProjectionStore($"Data Source={databasePath}"))
            {
                await store.InitializeAsync();
                var results = await ImportMainFixturesAsync(store);

                Assert.Equal(3, results.Count);
                Assert.True(results[0].Applied);
                Assert.True(results[1].Duplicate);
                Assert.True(results[2].Applied);
                Assert.True(await store.CanReadAsync());

                var response = await new AppViewService(store).GetBundleResponseAsync(BundleIdentity);
                Assert.NotNull(response);
                Assert.Equal("Fixture reading list (updated)", response.Name);
                Assert.Equal("bafy-bundle-v2", response.Source.Cid);
                Assert.Equal("fixture-rev-2", response.Source.Revision);
                Assert.Equal(2, response.Members.Count);
                Assert.Equal([0, 1], response.Members.Select(member => member.Position));
                Assert.Equal("syndication", response.Members[0].Projected?.Kind);
                Assert.Equal("post", response.Members[1].Projected?.Kind);
                Assert.Equal("bafyfixturecid", response.Members[1].Projected?.Metadata["cid"]);
            }
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task UpdateAndDeleteEventsChangeReadModelWithoutNetwork()
    {
        var databasePath = NewDatabasePath();
        try
        {
            using (var store = new ProjectionStore($"Data Source={databasePath}"))
            {
                await store.InitializeAsync();
                await ImportMainFixturesAsync(store);

                var beforeDelete = await new AppViewService(store).GetPublicBundleAsync(BundleIdentity);
                Assert.NotNull(beforeDelete);
                Assert.Equal("Fixture reading list (updated)", beforeDelete.Name);

                var deleted = await new FixtureImporter(store).ImportDirectoryAsync(FixtureRoot, "delete-event.json");
                Assert.Single(deleted);
                Assert.True(deleted[0].Applied);
                Assert.Null(await new AppViewService(store).GetPublicBundleAsync(BundleIdentity));
            }
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task AppViewContractAndReaderClientUseHttpEnvelope()
    {
        var databasePath = NewDatabasePath();
        try
        {
            using (var seedStore = new ProjectionStore($"Data Source={databasePath}"))
            {
                await seedStore.InitializeAsync();
                await ImportMainFixturesAsync(seedStore);
            }

            using (var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseSetting("ConnectionStrings:Projection", $"Data Source={databasePath}");
                }))
            using (var client = factory.CreateClient())
            {
                var readerClient = new AppViewClient(client);

                var bundle = await readerClient.GetBundleAsync(BundleIdentity);
                Assert.Equal("Fixture reading list (updated)", bundle.Name);
                Assert.Equal("Updated fixture post", bundle.Members[1].Label);
                Assert.Equal("A public fixture post.", bundle.Members[1].Projected?.Text);

                var missing = await client.GetAsync(
                    "/xrpc/me.lqdev.resourcegraph.appview.getBundle?bundle=at%3A%2F%2Fmissing");
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
                var error = await missing.Content.ReadFromJsonAsync<AppViewError>(AppViewJson.Options);
                Assert.Equal("not_found", error?.Error);
                var clientError = await Assert.ThrowsAsync<AppViewClientException>(
                    () => readerClient.GetBundleAsync("at://missing"));
                Assert.Contains("No indexed bundle", clientError.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task PrimitiveAppViewContractIsAdaptedFromPersistedRows()
    {
        var databasePath = NewDatabasePath();
        try
        {
            using (var store = new ProjectionStore($"Data Source={databasePath}"))
            {
                await store.InitializeAsync();
                await ImportMainFixturesAsync(store);
                var appView = new AppViewService(store);

                var result = await appView.ExpandPublicBundleAsync(
                    BundleIdentity,
                    ResourceGraph.Core.ExpansionLimits.Default);

                Assert.True(result.IsSuccess);
                Assert.NotNull(result.Value);
                Assert.Equal(2, result.Value!.Resources.Count);
                Assert.Equal("https://example.test/feeds/reading.xml", result.Value.Resources[0].Reference.Identity);
                Assert.Equal("at://did:plc:fixture/app.bsky.feed.post/3fixture", result.Value.Resources[1].Reference.Identity);
            }
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    private static async Task<IReadOnlyList<IngestionResult>> ImportMainFixturesAsync(ProjectionStore store) =>
        await new FixtureImporter(store).ImportDirectoryAsync(FixtureRoot);

    private static string FixtureRoot =>
        FindRepositoryRoot()
        ?? throw new InvalidOperationException("Could not find the checked-in fixtures.");

    private static string? FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "fixtures", "events.jsonl")))
            {
                return Path.Combine(current.FullName, "fixtures");
            }

            current = current.Parent;
        }

        return null;
    }

    private static string NewDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"at-resource-graph-appview-{Guid.NewGuid():N}.db");

    private static void DeleteDatabase(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-shm", "-wal" })
        {
            var candidate = path + suffix;
            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
    }
}
