using Amazon.Auth.AccessControlPolicy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.Retry;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Polly;
using Polly.Retry;
using Policy = Polly.Policy;
namespace FaceSearch.Infrastructure.Embedder;

public interface IEmbedderClient
{
    Task<float[]> EmbedTextAsync(string text, CancellationToken ct = default);
    Task<float[]> EmbedImageAsync(Stream imageStream, string fileName, CancellationToken ct = default);
    Task<float[]> EmbedFaceAsync(Stream imageStream, string fileName, CancellationToken ct = default);

    // Add these two diagnostics endpoints
    Task<StatusResponse> GetStatusAsync(CancellationToken ct = default);
    Task<SelfTestResponse> SelfTestAsync(CancellationToken ct = default);
}
public sealed class EmbedderClient : IEmbedderClient
{
    private readonly HttpClient _http;
    private readonly EmbedderOptions _opt;
    private readonly ILogger<EmbedderClient> _log;

    // Retry: brief backoff on transient HTTP failures/timeouts
    private readonly AsyncRetryPolicy _retry =
        Policy
            .Handle<HttpRequestException>()
            .Or<TaskCanceledException>()
            .WaitAndRetryAsync(new[]
            {
                TimeSpan.FromMilliseconds(200),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
            });

    // SINGLE constructor (typed HttpClient). Do not add another ctor.
    public EmbedderClient(HttpClient http, EmbedderOptions opt, ILogger<EmbedderClient> log)
    {
        _http = http;
        _opt = opt;
        _log = log;

        // Ensure BaseAddress is always valid even if DI misconfigured
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(_opt.BaseUrl, UriKind.Absolute);

        if (_opt.TimeoutSeconds > 0)
            _http.Timeout = TimeSpan.FromSeconds(_opt.TimeoutSeconds);

        _log.LogInformation("Embedder HttpClient BaseAddress = {Base}", _http.BaseAddress);
    }

    public async Task<StatusResponse> GetStatusAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("/_status", ct);
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<StatusResponse>(cancellationToken: ct);
        return doc ?? new StatusResponse { Status = "Unknown" };
    }

    public async Task<SelfTestResponse> SelfTestAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("/_selftest", ct);
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<SelfTestResponse>(cancellationToken: ct);
        return doc ?? new SelfTestResponse { Passed = false, Details = "No response" };
    }

    public Task<float[]> EmbedTextAsync(string text, CancellationToken ct = default) =>
        _retry.ExecuteAsync(async token =>
        {
            var resp = await _http.PostAsJsonAsync("/embed/text", new { text }, token);
            resp.EnsureSuccessStatusCode();
            var doc = await resp.Content.ReadFromJsonAsync<EmbedResponse>(cancellationToken: token);
            return doc!.vector;
        }, ct);

    public Task<float[]> EmbedImageAsync(Stream imageStream, string fileName, CancellationToken ct = default) =>
        _retry.ExecuteAsync(async token =>
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StreamContent(imageStream), "file", fileName);

            var resp = await _http.PostAsync("/embed/image", form, token);
            resp.EnsureSuccessStatusCode();
            var doc = await resp.Content.ReadFromJsonAsync<EmbedResponse>(cancellationToken: token);
            return doc!.vector;
        }, ct);

    public Task<float[]> EmbedFaceAsync(Stream imageStream, string fileName, CancellationToken ct = default) =>
        _retry.ExecuteAsync(async token =>
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StreamContent(imageStream), "file", fileName);

            var resp = await _http.PostAsync("/embed/face", form, token);
            resp.EnsureSuccessStatusCode();
            var doc = await resp.Content.ReadFromJsonAsync<EmbedResponse>(cancellationToken: token);
            return doc!.vector;
        }, ct);

    private sealed class EmbedResponse
    {
        public required float[] vector { get; init; }
    }
}