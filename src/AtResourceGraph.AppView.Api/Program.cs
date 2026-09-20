using AtResourceGraph.AppView.Application;
using AtResourceGraph.AppView.Contracts;
using ResourceGraph.AppView;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseKestrel(options => options.AddServerHeader = false);

var databasePath = builder.Configuration["AppView:DatabasePath"]
    ?? Environment.GetEnvironmentVariable("APPVIEW_DATABASE_PATH")
    ?? Path.Combine(AppContext.BaseDirectory, "data", "appview.db");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
var connectionString = builder.Configuration.GetConnectionString("Projection")
    ?? $"Data Source={databasePath}";

builder.Services.AddSingleton(new ProjectionStore(connectionString));
builder.Services.AddSingleton<AppViewService>();
builder.Services.AddSingleton<IResourceGraphAppView>(services =>
    services.GetRequiredService<AppViewService>());

var app = builder.Build();
var store = app.Services.GetRequiredService<ProjectionStore>();
await store.InitializeAsync();

if (bool.TryParse(builder.Configuration["AppView:SeedFixtures"], out var seedFixtures) && seedFixtures)
{
    var fixtureDirectory = builder.Configuration["AppView:FixtureDirectory"]
        ?? Environment.GetEnvironmentVariable("APPVIEW_FIXTURE_DIRECTORY")
        ?? Path.Combine(app.Environment.ContentRootPath, "fixtures");
    await new FixtureImporter(store).ImportDirectoryAsync(fixtureDirectory);
}

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

app.MapGet("/", () => Results.Json(new
{
    service = "AT Resource Graph AppView sample",
    contract = "draft me.lqdev.resourcegraph.appview.getBundle",
    offline = true
}, AppViewJson.Options));

app.MapGet("/healthz", () => Results.Json(new { status = "ok" }, AppViewJson.Options));

app.MapGet("/readyz", async (ProjectionStore projectionStore, CancellationToken cancellationToken) =>
{
    var ready = await projectionStore.CanReadAsync(cancellationToken);
    return ready
        ? Results.Json(new { status = "ready" }, AppViewJson.Options)
        : Results.Json(
            new AppViewError("not_ready", "The projection schema is not initialized."),
            AppViewJson.Options,
            statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapGet(
    "/xrpc/me.lqdev.resourcegraph.appview.getBundle",
    async (string? bundle, AppViewService appView, CancellationToken cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(bundle))
        {
            return Results.Json(
                new AppViewError("invalid_request", "Query parameter 'bundle' is required."),
                AppViewJson.Options,
                statusCode: StatusCodes.Status400BadRequest);
        }

        var response = await appView.GetBundleResponseAsync(bundle, cancellationToken);
        return response is null
            ? Results.Json(
                new AppViewError("not_found", $"No indexed bundle was found for '{bundle}'."),
                AppViewJson.Options,
                statusCode: StatusCodes.Status404NotFound)
            : Results.Json(response, AppViewJson.Options);
    });

app.Run();

public partial class Program;
