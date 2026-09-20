using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AtResourceGraph.AppView.Contracts;

namespace AtResourceGraph.Reader;

/// <summary>Typed HTTP-only client used by the separate Reader process.</summary>
public sealed class AppViewClient
{
    private readonly HttpClient httpClient;

    public AppViewClient(HttpClient httpClient)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<AppViewBundleResponse> GetBundleAsync(
        string bundleIdentity,
        CancellationToken cancellationToken = default)
    {
        var uri = $"/xrpc/me.lqdev.resourcegraph.appview.getBundle?bundle={Uri.EscapeDataString(bundleIdentity)}";
        using var response = await httpClient.GetAsync(uri, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<AppViewBundleResponse>(
                AppViewJson.Options,
                cancellationToken)
                ?? throw new AppViewClientException("The AppView returned an empty response.");
        }

        AppViewError? error;
        try
        {
            error = await response.Content.ReadFromJsonAsync<AppViewError>(
                AppViewJson.Options,
                cancellationToken);
        }
        catch (JsonException)
        {
            error = null;
        }
        throw new AppViewClientException(
            error?.Message ?? $"The AppView request failed with HTTP {(int)response.StatusCode}.",
            response.StatusCode);
    }
}

public sealed class AppViewClientException : Exception
{
    public AppViewClientException(string message, HttpStatusCode? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
}
