using System.Drawing;
using System.Net.Http;
using HtmlAgilityPack;
using Polly;
using Polly.Retry;

// Test with sample URLs - including the specific URL that was failing
var testUrls = new[]
{
    "https://jpg6.su/img/emKmry"  // This should find https://simp1.selti-delivery.ru/images/8E2632E5-490A-4815-AB19-AA4BA7733997.jpg
};

var outputFolder = Path.Combine(Directory.GetCurrentDirectory(), "test");
Directory.CreateDirectory(outputFolder);

Console.WriteLine($"Output folder: {outputFolder}");
Console.WriteLine($"Testing {testUrls.Length} URLs");
Console.WriteLine();

// HTTP client with retry policy
var retryPolicy = Policy
    .Handle<HttpRequestException>()
    .Or<TaskCanceledException>()
    .WaitAndRetryAsync(
        retryCount: 3,
        sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
        onRetry: (exception, timeSpan, retryCount, context) =>
        {
            Console.WriteLine($"  ⚠️  Retry {retryCount}/3 after {timeSpan.TotalSeconds}s: {exception.Message}");
        });

using var httpClient = new HttpClient();
httpClient.Timeout = TimeSpan.FromSeconds(30);
httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

var downloadedImages = 0;
var skippedImages = 0;
var errorUrls = 0;
var minSize = 255; // Minimum dimension (width or height)

foreach (var url in testUrls)
{
    Console.WriteLine($"Processing: {url}");
    
    try
    {
        var images = await ExtractImagesFromUrlAsync(url, httpClient, retryPolicy);

        if (images.Count == 0)
        {
            Console.WriteLine($"  ⚠️  No images found");
            errorUrls++;
            continue;
        }

        foreach (var imageInfo in images)
        {
            Console.WriteLine($"  Found image: {imageInfo.Url} ({imageInfo.Width}x{imageInfo.Height})");

            if (imageInfo.Width >= minSize || imageInfo.Height >= minSize)
            {
                var saved = await SaveImageAsync(imageInfo, outputFolder, "test");
                if (saved)
                {
                    downloadedImages++;
                    Console.WriteLine($"  ✅ Downloaded: {imageInfo.Url} ({imageInfo.Width}x{imageInfo.Height})");
                }
                else
                {
                    skippedImages++;
                    Console.WriteLine($"  ⏭️  Skipped (already exists or error)");
                }
            }
            else
            {
                skippedImages++;
                Console.WriteLine($"  ⏭️  Skipped (too small): {imageInfo.Url} ({imageInfo.Width}x{imageInfo.Height})");
            }
        }
    }
    catch (Exception ex)
    {
        errorUrls++;
        Console.WriteLine($"  ❌ Error: {ex.Message}");
        if (ex.InnerException != null)
        {
            Console.WriteLine($"     Inner: {ex.InnerException.Message}");
        }
    }
    
    Console.WriteLine();
}

Console.WriteLine("=== Test Summary ===");
Console.WriteLine($"Images downloaded: {downloadedImages}");
Console.WriteLine($"Images skipped: {skippedImages}");
Console.WriteLine($"URL errors: {errorUrls}");
Console.WriteLine($"Output folder: {outputFolder}");

async Task<List<ImageInfo>> ExtractImagesFromUrlAsync(string url, HttpClient httpClient, AsyncRetryPolicy retryPolicy)
{
    var images = new List<ImageInfo>();

    try
    {
        // First, try to fetch as direct image
        var isImage = await TryAsDirectImageAsync(url, httpClient, retryPolicy, images);
        if (isImage)
        {
            Console.WriteLine($"  ✓ Direct image detected");
            return images;
        }

        Console.WriteLine($"  → Not a direct image, parsing HTML...");

        // If not a direct image, fetch as HTML and parse
        var htmlContent = await retryPolicy.ExecuteAsync(async () =>
        {
            var response = await httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        });

        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(htmlContent);

        // Extract all img tags
        var imgNodes = htmlDoc.DocumentNode.SelectNodes("//img[@src]") ?? 
                      htmlDoc.DocumentNode.SelectNodes("//img[@data-src]") ??
                      htmlDoc.DocumentNode.SelectNodes("//img[@data-lazy-src]") ??
                      new HtmlNodeCollection(null);

        var baseUri = new Uri(url);
        Console.WriteLine($"  Found {imgNodes.Count} <img> tags");

        foreach (var imgNode in imgNodes)
        {
            var src = imgNode.GetAttributeValue("src", null) ?? 
                     imgNode.GetAttributeValue("data-src", null) ??
                     imgNode.GetAttributeValue("data-lazy-src", null);

            if (string.IsNullOrWhiteSpace(src))
                continue;

            try
            {
                var imageUrl = new Uri(baseUri, src).AbsoluteUri;
                Console.WriteLine($"    Trying image: {imageUrl}");
                await TryAsDirectImageAsync(imageUrl, httpClient, retryPolicy, images);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    ⚠️  Invalid image URL: {src} - {ex.Message}");
            }
        }

        // Also check for picture/source tags
        var sourceNodes = htmlDoc.DocumentNode.SelectNodes("//source[@srcset]") ?? 
                         htmlDoc.DocumentNode.SelectNodes("//source[@src]") ??
                         new HtmlNodeCollection(null);

        Console.WriteLine($"  Found {sourceNodes.Count} <source> tags");

        foreach (var sourceNode in sourceNodes)
        {
            var srcset = sourceNode.GetAttributeValue("srcset", null);
            var src = sourceNode.GetAttributeValue("src", null);

            if (!string.IsNullOrWhiteSpace(srcset))
            {
                // Parse srcset (format: "url1 size1, url2 size2")
                var entries = srcset.Split(',');
                foreach (var entry in entries)
                {
                    var parts = entry.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0)
                    {
                        try
                        {
                            var imageUrl = new Uri(baseUri, parts[0]).AbsoluteUri;
                            Console.WriteLine($"    Trying srcset image: {imageUrl}");
                            await TryAsDirectImageAsync(imageUrl, httpClient, retryPolicy, images);
                        }
                        catch { }
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(src))
            {
                try
                {
                    var imageUrl = new Uri(baseUri, src).AbsoluteUri;
                    Console.WriteLine($"    Trying source image: {imageUrl}");
                    await TryAsDirectImageAsync(imageUrl, httpClient, retryPolicy, images);
                }
                catch { }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"    ⚠️  Error extracting images from {url}: {ex.Message}");
    }

    return images;
}

async Task<bool> TryAsDirectImageAsync(string url, HttpClient httpClient, AsyncRetryPolicy retryPolicy, List<ImageInfo> images)
{
    try
    {
        var response = await retryPolicy.ExecuteAsync(async () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Accept", "image/*");
            var resp = await httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            return resp;
        });

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return false;

        // Download image bytes and check dimensions
        var imageBytes = await response.Content.ReadAsByteArrayAsync();
        
        using var imageStream = new MemoryStream(imageBytes);
        using var image = Image.FromStream(imageStream);
        
        images.Add(new ImageInfo
        {
            Url = url,
            Width = image.Width,
            Height = image.Height,
            ContentType = contentType,
            ImageBytes = imageBytes // Store bytes to avoid re-downloading
        });

        return true;
    }
    catch
    {
        return false;
    }
}

async Task<bool> SaveImageAsync(ImageInfo imageInfo, string outputFolder, string title)
{
    try
    {
        // Sanitize title for folder name
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitizedTitle = string.Join("_", title.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries))
            .Replace(" ", "_");
        
        if (sanitizedTitle.Length > 50)
            sanitizedTitle = sanitizedTitle.Substring(0, 50);

        var titleFolder = Path.Combine(outputFolder, sanitizedTitle);
        Directory.CreateDirectory(titleFolder);

        // Generate filename from URL
        var uri = new Uri(imageInfo.Url);
        var fileName = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName) || !Path.HasExtension(fileName))
        {
            var ext = GetExtensionFromContentType(imageInfo.ContentType);
            fileName = $"image_{Guid.NewGuid():N}{ext}";
        }
        else
        {
            // Sanitize filename
            var fileBaseName = Path.GetFileNameWithoutExtension(fileName);
            var fileExt = Path.GetExtension(fileName);
            fileBaseName = string.Join("_", fileBaseName.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
            fileName = $"{fileBaseName}{fileExt}";
        }

        var filePath = Path.Combine(titleFolder, fileName);

        // Skip if already exists
        if (File.Exists(filePath))
        {
            Console.WriteLine($"    File already exists: {filePath}");
            return false;
        }

        // Save image bytes (already downloaded during dimension check)
        if (imageInfo.ImageBytes != null && imageInfo.ImageBytes.Length > 0)
        {
            await File.WriteAllBytesAsync(filePath, imageInfo.ImageBytes);
            Console.WriteLine($"    Saved to: {filePath}");
            return true;
        }
        else
        {
            // Fallback: download again if bytes weren't stored
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30);
            var imageBytes = await httpClient.GetByteArrayAsync(imageInfo.Url);
            await File.WriteAllBytesAsync(filePath, imageBytes);
            Console.WriteLine($"    Saved to: {filePath} (re-downloaded)");
            return true;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"    ⚠️  Error saving {imageInfo.Url}: {ex.Message}");
        return false;
    }
}

string GetExtensionFromContentType(string contentType)
{
    return contentType.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        _ => ".jpg"
    };
}

class ImageInfo
{
    public string Url { get; set; } = default!;
    public int Width { get; set; }
    public int Height { get; set; }
    public string ContentType { get; set; } = "";
    public byte[]? ImageBytes { get; set; }
}

