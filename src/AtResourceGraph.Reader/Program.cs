using System.Net;
using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Globalization;
using AtResourceGraph.AppView.Contracts;
using AtResourceGraph.Reader;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseKestrel(options => options.AddServerHeader = false);
var apiBaseUrl = builder.Configuration["Reader:AppViewBaseUrl"]
    ?? Environment.GetEnvironmentVariable("APPVIEW_API_BASE_URL")
    ?? "http://localhost:5070/";
if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var parsedApiBaseUrl) ||
    parsedApiBaseUrl.Scheme is not ("http" or "https"))
{
    throw new InvalidOperationException("Reader:AppViewBaseUrl must be an absolute HTTP(S) URL.");
}

builder.Services.AddHttpClient<AppViewClient>(client =>
{
    client.BaseAddress = parsedApiBaseUrl;
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    client.Timeout = TimeSpan.FromSeconds(5);
});

var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'none'; style-src 'unsafe-inline'; connect-src 'self';";
    await next();
});

const string sampleBundleIdentity = "at://did:plc:fixture/me.lqdev.resourcegraph.bundle/reading-list";

app.MapGet("/healthz", () => Results.Json(new { status = "ok" }, AppViewJson.Options));
app.MapGet("/", async (AppViewClient client, CancellationToken cancellationToken) =>
{
    try
    {
        var bundle = await client.GetBundleAsync(sampleBundleIdentity, cancellationToken);
        return Results.Content(RenderBundle(bundle), "text/html; charset=utf-8");
    }
    catch (AppViewClientException exception)
    {
        return Results.Content(
            RenderError($"AppView error: {exception.Message}"),
            "text/html; charset=utf-8",
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (HttpRequestException exception)
    {
        return Results.Content(
            RenderError($"AppView unavailable: {exception.Message}"),
            "text/html; charset=utf-8",
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (TaskCanceledException)
    {
        return Results.Content(
            RenderError("AppView request timed out."),
            "text/html; charset=utf-8",
            statusCode: StatusCodes.Status504GatewayTimeout);
    }
});

app.Run();

static string RenderBundle(AppViewBundleResponse bundle)
{
    Func<string, string> encode = text => HtmlEncoder.Default.Encode(text);
    var members = string.Join(
        Environment.NewLine,
        bundle.Members.Select(member =>
            $"""
            <li>
              <strong>{encode(member.Position.ToString(CultureInfo.InvariantCulture))}. {encode(member.Projected?.Title ?? member.Identity)}</strong>
              <div>Kind: <code>{encode(member.Kind)}</code> · Identity: <code>{encode(member.Identity)}</code></div>
              <div>{encode(member.Projected?.Text ?? "No projected text.")}</div>
              <div>Link: <code>{encode(member.Projected?.Link ?? "none")}</code> · Published: <code>{encode(FormatPublished(member.Projected?.Published))}</code></div>
              <div>Metadata: <code>{encode(FormatMetadata(member.Projected?.Metadata))}</code></div>
            </li>
            """));
    var diagnostics = bundle.Diagnostics.Count == 0
        ? "<p>None</p>"
        : $"<ul>{string.Join("", bundle.Diagnostics.Select(diagnostic => RenderDiagnostic(diagnostic, encode)))}</ul>";
    const string template =
        """
        <!doctype html>
        <html lang="en">
          <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>@@NAME@@ - AT Resource Graph Reader</title>
            <style>
              body { font-family: system-ui, sans-serif; margin: 2rem; max-width: 60rem; }
              code { background: #eee; padding: .1rem .25rem; }
              li { margin-block: 1rem; }
            </style>
          </head>
          <body>
            <h1>@@NAME@@</h1>
            <p>@@DESCRIPTION@@</p>
            <p>Source CID: <code>@@CID@@</code><br>
            Source revision: <code>@@REVISION@@</code><br>
            Indexed: <code>@@INDEXED@@</code></p>
            <h2>Ordered members</h2>
            <ol>@@MEMBERS@@</ol>
            <h2>Diagnostics</h2>
            @@DIAGNOSTICS@@
            <p>This Reader only calls the AppView HTTP API; it has no SQLite access.</p>
          </body>
        </html>
        """;
    return template
        .Replace("@@NAME@@", encode(bundle.Name), StringComparison.Ordinal)
        .Replace("@@DESCRIPTION@@", encode(bundle.Description ?? "No description."), StringComparison.Ordinal)
        .Replace("@@CID@@", encode(bundle.Source.Cid ?? "none"), StringComparison.Ordinal)
        .Replace("@@REVISION@@", encode(bundle.Source.Revision ?? "none"), StringComparison.Ordinal)
        .Replace("@@INDEXED@@", encode(bundle.IndexedAt.ToString("O")), StringComparison.Ordinal)
        .Replace("@@MEMBERS@@", members, StringComparison.Ordinal)
        .Replace("@@DIAGNOSTICS@@", diagnostics, StringComparison.Ordinal);
}

static string FormatMetadata(IReadOnlyDictionary<string, string>? metadata) =>
    string.Join(", ", (metadata ?? new Dictionary<string, string>())
        .Select(pair => $"{pair.Key}={pair.Value}"));

static string FormatPublished(DateTimeOffset? published) =>
    published?.ToString("O", CultureInfo.InvariantCulture) ?? "none";

static string RenderDiagnostic(AppViewDiagnostic diagnostic, Func<string, string> encode) =>
    $"<li><code>{encode(diagnostic.Severity)}</code> {encode(diagnostic.Code)}: {encode(diagnostic.Message)}</li>";

static string RenderError(string message) =>
    $"""
    <!doctype html>
    <html lang="en">
      <head><meta charset="utf-8"><title>AT Resource Graph Reader</title></head>
      <body><h1>Reader unavailable</h1><p>{HtmlEncoder.Default.Encode(message)}</p></body>
    </html>
    """;
