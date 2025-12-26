using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FaceSearch.Infrastructure.Embedder;

/// <summary>
/// Load-balanced embedder client that distributes requests across multiple embedder instances.
/// Uses round-robin or random selection for load balancing.
/// </summary>
public sealed class LoadBalancedEmbedderClient : IEmbedderClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly EmbedderOptions _opt;
    private readonly ILogger<LoadBalancedEmbedderClient> _log;
    private readonly Uri[] _baseUrls;
    private int _roundRobinIndex = 0;
    private readonly object _roundRobinLock = new object();
    private readonly Random _random = new Random();

    // Retry policy for transient failures
    private readonly AsyncRetryPolicy _retry = Policy
        .Handle<HttpRequestException>()
        .Or<TaskCanceledException>()
        .WaitAndRetryAsync(new[]
        {
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
        });

    public LoadBalancedEmbedderClient(
        IHttpClientFactory httpClientFactory,
        IOptions<EmbedderOptions> opt,
        ILogger<LoadBalancedEmbedderClient> log)
    {
        _httpClientFactory = httpClientFactory;
        _opt = opt.Value;
        _log = log;

        // Build list of URLs
        if (_opt.BaseUrls != null && _opt.BaseUrls.Length > 0)
        {
            _baseUrls = _opt.BaseUrls
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Select(url => new Uri(url.TrimEnd('/'), UriKind.Absolute))
                .ToArray();
        }
        else
        {
            // Fallback to single BaseUrl for backward compatibility
            if (string.IsNullOrWhiteSpace(_opt.BaseUrl))
                throw new InvalidOperationException("Embedder BaseUrl or BaseUrls must be configured");
            _baseUrls = new[] { new Uri(_opt.BaseUrl.TrimEnd('/'), UriKind.Absolute) };
        }

        if (_baseUrls.Length == 0)
            throw new InvalidOperationException("At least one embedder URL must be configured");

        _log.LogInformation("LoadBalancedEmbedderClient initialized with {Count} embedder instance(s): {Urls}",
            _baseUrls.Length, string.Join(", ", _baseUrls.Select(u => u.ToString())));
    }

    private Uri GetNextBaseUrl()
    {
        if (_baseUrls.Length == 1)
            return _baseUrls[0];

        if (_opt.LoadBalancingStrategy == "random")
        {
            return _baseUrls[_random.Next(_baseUrls.Length)];
        }
        else // round-robin (default)
        {
            lock (_roundRobinLock)
            {
                var url = _baseUrls[_roundRobinIndex];
                _roundRobinIndex = (_roundRobinIndex + 1) % _baseUrls.Length;
                return url;
            }
        }
    }

    private HttpClient CreateHttpClient(Uri baseUrl)
    {
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = baseUrl;
        if (_opt.TimeoutSeconds > 0)
            client.Timeout = TimeSpan.FromSeconds(_opt.TimeoutSeconds);
        return client;
    }

    public async Task<StatusResponse> GetStatusAsync(CancellationToken ct = default)
    {
        // Try first instance for status
        var url = _baseUrls[0];
        using var client = CreateHttpClient(url);
        var resp = await client.GetAsync("/_status", ct);
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<StatusResponse>(cancellationToken: ct);
        return doc ?? new StatusResponse { Status = "Unknown" };
    }

    public async Task<SelfTestResponse> SelfTestAsync(CancellationToken ct = default)
    {
        // Try first instance for self-test
        var url = _baseUrls[0];
        using var client = CreateHttpClient(url);
        var resp = await client.GetAsync("/_selftest", ct);
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<SelfTestResponse>(cancellationToken: ct);
        return doc ?? new SelfTestResponse { Passed = false, Details = "No response" };
    }

    public Task<float[]> EmbedTextAsync(string text, CancellationToken ct = default) =>
        _retry.ExecuteAsync(async token =>
        {
            var baseUrl = GetNextBaseUrl();
            using var client = CreateHttpClient(baseUrl);
            var resp = await client.PostAsJsonAsync("/embed/text", new { text }, token);
            resp.EnsureSuccessStatusCode();
            var doc = await resp.Content.ReadFromJsonAsync<EmbedResponse>(cancellationToken: token);
            return doc!.vector;
        }, ct);

    public Task<float[]> EmbedImageAsync(Stream imageStream, string fileName, CancellationToken ct = default) =>
        TryAllInstancesAsync(async (client, token) =>
        {
            // Copy stream to MemoryStream to avoid disposal issues during retries
            imageStream.Position = 0;
            using var memoryStream = new MemoryStream();
            await imageStream.CopyToAsync(memoryStream, token);
            memoryStream.Position = 0;
            
            using var form = new MultipartFormDataContent();
            form.Add(new StreamContent(memoryStream), "file", fileName);

            var resp = await client.PostAsync("/embed/image", form, token);
            resp.EnsureSuccessStatusCode();
            var doc = await resp.Content.ReadFromJsonAsync<EmbedResponse>(cancellationToken: token);
            return doc!.vector;
        }, ct);

    public Task<float[]> EmbedFaceAsync(Stream imageStream, string fileName, CancellationToken ct = default) =>
        TryAllInstancesAsync(async (client, token) =>
        {
            // Copy stream to MemoryStream to avoid disposal issues during retries
            imageStream.Position = 0;
            using var memoryStream = new MemoryStream();
            await imageStream.CopyToAsync(memoryStream, token);
            memoryStream.Position = 0;
            
            using var form = new MultipartFormDataContent();
            form.Add(new StreamContent(memoryStream), "file", fileName);

            var resp = await client.PostAsync("/embed/face", form, token);
            resp.EnsureSuccessStatusCode();
            var doc = await resp.Content.ReadFromJsonAsync<EmbedResponse>(cancellationToken: token);
            return doc!.vector;
        }, ct);

    public Task<IReadOnlyList<FaceDetectionResult>> DetectFacesAsync(
        Stream imageStream,
        string fileName,
        bool femaleOnly = true,
        CancellationToken ct = default) =>
        TryAllInstancesAsync(async (client, token) =>
        {
            // Copy stream to MemoryStream to avoid disposal issues during retries
            imageStream.Position = 0;
            using var memoryStream = new MemoryStream();
            await imageStream.CopyToAsync(memoryStream, token);
            memoryStream.Position = 0;
            
            using var form = new MultipartFormDataContent();
            form.Add(new StreamContent(memoryStream), "file", fileName);

            var resp = await client.PostAsync($"/embed/face/multi?female_only={(femaleOnly ? "true" : "false")}", form, token);
            resp.EnsureSuccessStatusCode();
            var doc = await resp.Content.ReadFromJsonAsync<FaceDetectionsResponse>(cancellationToken: token);
            return (IReadOnlyList<FaceDetectionResult>)(doc?.Faces ?? Array.Empty<FaceDetectionResult>());
        }, ct);

    /// <summary>
    /// Tries all embedder instances in order until one succeeds. If all fail, throws the last exception.
    /// This provides automatic failover if some instances are not available.
    /// </summary>
    private async Task<T> TryAllInstancesAsync<T>(Func<HttpClient, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        Exception? lastException = null;
        var urlsToTry = _baseUrls.ToArray(); // Copy array to avoid modification during iteration

        foreach (var baseUrl in urlsToTry)
        {
            try
            {
                using var client = CreateHttpClient(baseUrl);
                return await operation(client, ct);
            }
            catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException socketEx && 
                                                   socketEx.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused)
            {
                // Connection refused - instance not running, try next
                _log.LogDebug("Embedder instance {Url} is not available (connection refused), trying next instance...", baseUrl);
                lastException = ex;
                continue;
            }
            catch (TaskCanceledException ex)
            {
                // Timeout - try next instance
                _log.LogDebug("Embedder instance {Url} timed out, trying next instance...", baseUrl);
                lastException = ex;
                continue;
            }
            catch (HttpRequestException ex)
            {
                // Other HTTP errors - try next instance
                _log.LogDebug(ex, "Embedder instance {Url} returned error, trying next instance...", baseUrl);
                lastException = ex;
                continue;
            }
        }

        // All instances failed
        _log.LogError(lastException, "All {Count} embedder instances failed. Last error from {LastUrl}", 
            urlsToTry.Length, urlsToTry.LastOrDefault());
        throw lastException ?? new InvalidOperationException($"All {urlsToTry.Length} embedder instances are unavailable");
    }

    private sealed class EmbedResponse
    {
        public required float[] vector { get; init; }
    }
}

