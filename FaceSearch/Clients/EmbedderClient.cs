#region Client Implementation

using FaceSearch.Infrastructure.Embedder;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
public sealed class EmbedderClient : IEmbedderClient
{
    private readonly HttpClient _http;
    private readonly EmbedderOptions _opt;
    private readonly JsonSerializerOptions _json;
    private readonly ILogger<EmbedderClient>? _log;

    public EmbedderClient(HttpClient httpClient, EmbedderOptions options, ILogger<EmbedderClient>? logger = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _opt = options ?? throw new ArgumentNullException(nameof(options));
        _log = logger;

        // Configure base address & default headers once.
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(_opt.BaseUrl);
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(_opt.ApiKeyHeader) && !string.IsNullOrWhiteSpace(_opt.ApiKeyValue))
        {
            _http.DefaultRequestHeaders.Remove(_opt.ApiKeyHeader);
            _http.DefaultRequestHeaders.Add(_opt.ApiKeyHeader, _opt.ApiKeyValue);
        }

        _json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };
    }

    public Task<EmbedTextResponse> EmbedTextAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        if (texts is null) throw new ArgumentNullException(nameof(texts));
        var payload = new EmbedTextRequest { Texts = texts.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() };
        if (payload.Texts.Count == 0) throw new ArgumentException("At least one non-empty text is required", nameof(texts));
        return SendJsonAsync<EmbedTextRequest, EmbedTextResponse>(HttpMethod.Post, "/embed/text", payload, ct);
    }

    public Task<EmbedImageResponse> EmbedImageAsync(ImageInput image, CancellationToken ct = default)
        => SendImageAsync<EmbedImageResponse>("/embed/image", image, ct);

    public Task<EmbedFaceResponse> EmbedFaceAsync(ImageInput image, CancellationToken ct = default)
        => SendImageAsync<EmbedFaceResponse>("/embed/face", image, ct);

    public Task<StatusResponse> GetStatusAsync(CancellationToken ct = default)
        => SendAsync<StatusResponse>(HttpMethod.Get, "/_status", null, ct);

    public Task<SelfTestResponse> SelfTestAsync(CancellationToken ct = default)
        => SendAsync<SelfTestResponse>(HttpMethod.Get, "/_selftest", null, ct);

    // ---- Core HTTP helpers ----
    private async Task<TResponse> SendJsonAsync<TRequest, TResponse>(HttpMethod method, string uri, TRequest payload, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_opt.TimeoutSeconds));

        var json = JsonSerializer.Serialize(payload, _json);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await SendWithRetriesAsync<TResponse>(() => new HttpRequestMessage(method, uri) { Content = content }, cts.Token);
    }

    private async Task<TResponse> SendImageAsync<TResponse>(string uri, ImageInput input, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_opt.TimeoutSeconds));

        using var form = new MultipartFormDataContent();
        StreamContent fileContent;

        if (!string.IsNullOrEmpty(input.FilePath))
        {
            var fs = File.OpenRead(input.FilePath); // will be disposed by StreamContent
            fileContent = new StreamContent(fs);
        }
        else if (!input.Bytes.IsEmpty)
        {
            var ms = new MemoryStream(input.Bytes.ToArray());
            fileContent = new StreamContent(ms);
        }
        else
        {
            throw new ArgumentException("ImageInput must have FilePath or Bytes", nameof(input));
        }

        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg"); // Accepts JPEG/PNG; server can infer
        form.Add(fileContent, "file", input.FileName ?? "image.jpg");

        return await SendWithRetriesAsync<TResponse>(() => new HttpRequestMessage(HttpMethod.Post, uri) { Content = form }, cts.Token);
    }

    private async Task<TResponse> SendAsync<TResponse>(HttpMethod method, string uri, HttpContent? content, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_opt.TimeoutSeconds));
        return await SendWithRetriesAsync<TResponse>(() => new HttpRequestMessage(method, uri) { Content = content }, cts.Token);
    }

    private async Task<TResponse> SendWithRetriesAsync<TResponse>(Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        var attempt = 0;
        var sw = Stopwatch.StartNew();

        while (true)
        {
            attempt++;
            try
            {
                using var req = requestFactory();
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if (IsTransient(resp.StatusCode) && attempt <= _opt.MaxRetries)
                {
                    await DelayBackoffAsync(attempt, ct).ConfigureAwait(false);
                    continue;
                }

                resp.EnsureSuccessStatusCode();

                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var result = await JsonSerializer.DeserializeAsync<TResponse>(stream, _json, ct).ConfigureAwait(false);
                if (result is null)
                    throw new InvalidOperationException("Embedder returned empty or invalid JSON body");

                _log?.LogInformation("Embedder request {Method} {Path} succeeded in {ElapsedMs} ms (attempt {Attempt})",
                    req.Method, req.RequestUri, sw.ElapsedMilliseconds, attempt);
                return result;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt <= _opt.MaxRetries)
            {
                _log?.LogWarning("Embedder timeout; retrying (attempt {Attempt}/{Max})", attempt, _opt.MaxRetries);
                await DelayBackoffAsync(attempt, ct).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException ex) when (attempt <= _opt.MaxRetries)
            {
                _log?.LogWarning(ex, "Embedder HTTP error; retrying (attempt {Attempt}/{Max})", attempt, _opt.MaxRetries);
                await DelayBackoffAsync(attempt, ct).ConfigureAwait(false);
                continue;
            }
        }
    }

    private static bool IsTransient(HttpStatusCode status)
        => status == HttpStatusCode.TooManyRequests
           || (int)status >= 500;

    private async Task DelayBackoffAsync(int attempt, CancellationToken ct)
    {
        var expo = Math.Pow(2, attempt - 1);
        var jitter = Random.Shared.NextDouble() * 0.3 + 0.85; // 0.85x–1.15x
        var delayMs = (int)Math.Min(_opt.BaseDelayMs * expo * jitter, 4000);
        await Task.Delay(delayMs, ct).ConfigureAwait(false);
    }
}

#endregion

#region DI Extension



public static class EmbedderClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers IEmbedderClient with HttpClient factory and configuration binding.
    /// </summary>
    public static IServiceCollection AddEmbedderClient(this IServiceCollection services, Action<EmbedderOptions>? configure = null)
    {
        var opts = new EmbedderOptions();
        configure?.Invoke(opts);

        services.AddSingleton(opts);

        services.AddHttpClient<IEmbedderClient, EmbedderClient>((sp, http) =>
        {
            // BaseAddress and headers are set inside EmbedderClient ctor using EmbedderOptions.
            // Here we can only influence primary handler policies if needed.
            http.Timeout = Timeout.InfiniteTimeSpan; // per-request timeouts handled in client
        });

        return services;
    }
}

#endregion


#region Minimal Unit Test (pseudo using xUnit)

/*
public class EmbedderClientTests
{
    [Fact]
    public async Task Status_Should_Return_Devices()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddEmbedderClient(o => o.BaseUrl = "http://localhost:8090")
            .BuildServiceProvider();

        var client = services.GetRequiredService<IEmbedderClient>();
        var res = await client.GetStatusAsync();
        Assert.NotNull(res);
    }
}
*/

#endregion