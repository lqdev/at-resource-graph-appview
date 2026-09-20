using AtResourceGraph.AppView.Application;

var fixtureDirectory = args.Length > 0
    ? args[0]
    : Path.Combine(Directory.GetCurrentDirectory(), "fixtures");
var databasePath = args.Length > 1
    ? args[1]
    : Path.Combine(Directory.GetCurrentDirectory(), "data", "appview.db");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);

var store = new ProjectionStore($"Data Source={databasePath}");
await store.InitializeAsync();
var results = await new FixtureImporter(store).ImportDirectoryAsync(fixtureDirectory);
foreach (var result in results)
{
    Console.WriteLine(
        $"{result.EventId}: {(result.Duplicate ? "duplicate" : result.Applied ? "applied" : $"rejected ({result.RejectionCode})")}");
}
