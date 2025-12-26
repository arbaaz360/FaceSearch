using System.Drawing;
using System.Net;
using System.Net.Http;
using System.Linq;
using HtmlAgilityPack;
using Infrastructure.Mongo.Models;
using MongoDB.Driver;
using Polly;
using Polly.Retry;
using Microsoft.Playwright;
using System.Text.Json;

var connectionString = "mongodb://127.0.0.1:27017";
var databaseName = "facesearch";
var collectionName = "jpg6_data";
var outputFolder = @"X:\simpcity";

// Create output folder
Directory.CreateDirectory(outputFolder);

var client = new MongoClient(connectionString);
var database = client.GetDatabase(databaseName);
var collection = database.GetCollection<Jpg6DataMongo>(collectionName);

Console.WriteLine($"Connecting to MongoDB: {connectionString}");
Console.WriteLine($"Database: {databaseName}, Collection: {collectionName}");
Console.WriteLine($"Output folder: {outputFolder}");
Console.WriteLine();

// HTTP client with retry policy
var retryPolicy = Policy
    .Handle<HttpRequestException>()
    .Or<TaskCanceledException>()
    .Or<WebException>()
    .WaitAndRetryAsync(
        retryCount: 0,
        sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
        onRetry: (exception, timeSpan, retryCount, context) =>
        {
            Console.WriteLine($"  ⚠️  Retry {retryCount}/0 after {timeSpan.TotalSeconds}s: {exception.Message}");
        });

using var httpClient = new HttpClient(new HttpClientHandler 
{ 
    AllowAutoRedirect = true,
    MaxAutomaticRedirections = 10
});
httpClient.Timeout = TimeSpan.FromSeconds(30);
httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

// Initialize Playwright for pages that need JavaScript execution
IPlaywright? playwright = null;
IBrowser? browser = null;
try
{
    playwright = await Playwright.CreateAsync();
    browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
    {
        Headless = true,
        Args = new[] 
        {
            "--disable-blink-features=AutomationControlled", // Hide automation
            "--disable-dev-shm-usage",
            "--no-sandbox",
            "--disable-setuid-sandbox"
        }
    });
    Console.WriteLine("✓ Playwright browser initialized for JavaScript-enabled page loading");
}
catch (Exception ex)
{
    Console.WriteLine($"⚠️  Warning: Could not initialize Playwright browser: {ex.Message}");
    Console.WriteLine($"   Falling back to basic HTTP client. Some pages may not load correctly.");
}

// Get documents that need processing (pending, processing, or no status set yet)
// Documents without Status field will have default value (pending)
var filter = Builders<Jpg6DataMongo>.Filter.Or(
    Builders<Jpg6DataMongo>.Filter.In(x => x.Status, 
        new[] { Jpg6DocumentStatus.pending, Jpg6DocumentStatus.processing }),
    Builders<Jpg6DataMongo>.Filter.Exists(x => x.Status, false)
);
var documents = await collection.Find(filter)
    .ToListAsync();

Console.WriteLine($"Found {documents.Count} documents to process (pending/processing)");
Console.WriteLine();

var totalUrls = 0;
var processedUrls = 0;
var downloadedImages = 0;
var skippedImages = 0;
var errorUrls = 0;
var minSize = 255; // Minimum dimension (width or height)
var processedImageUrls = new HashSet<string>(); // Track processed image URLs to avoid duplicates
var totalDocuments = documents.Count;
var processedDocuments = 0;

// Calculate total URLs first
foreach (var doc in documents)
{
    if (doc.Jpg6Urls != null && doc.Jpg6Urls.Count > 0)
        totalUrls += doc.Jpg6Urls.Count;
}

Console.WriteLine($"Total documents to process: {totalDocuments}");
Console.WriteLine($"Total URLs to process: {totalUrls}");
Console.WriteLine($"Minimum image size: {minSize}x{minSize} pixels");
Console.WriteLine();

foreach (var doc in documents)
{
    processedDocuments++;
    
    if (doc.Jpg6Urls == null || doc.Jpg6Urls.Count == 0)
    {
        Console.WriteLine($"[{processedDocuments}/{totalDocuments}] Skipping '{doc.Title}' - No URLs");
        // Mark as processed if no URLs
        await MarkDocumentProcessedAsync(collection, doc.Id);
        continue;
    }

    // Mark document as processing
    await MarkDocumentProcessingAsync(collection, doc.Id);
    
    // Reload document to get latest state
    var currentDoc = await collection.Find(x => x.Id == doc.Id).FirstOrDefaultAsync();
    if (currentDoc == null) continue;
    
    // Initialize URL statuses if not already done
    if (currentDoc.UrlStatuses == null || currentDoc.UrlStatuses.Count == 0)
    {
        currentDoc.UrlStatuses = currentDoc.Jpg6Urls.Select(url => new Jpg6UrlStatusInfo
        {
            Url = url,
            Status = Jpg6UrlStatus.pending
        }).ToList();
        await UpdateDocumentUrlStatusesAsync(collection, currentDoc.Id, currentDoc.UrlStatuses);
    }
    else
    {
        // Reset any "processing" URLs back to "pending" (in case of crash/restart)
        var needsUpdate = false;
        foreach (var urlStatus in currentDoc.UrlStatuses)
        {
            if (urlStatus.Status == Jpg6UrlStatus.processing)
            {
                urlStatus.Status = Jpg6UrlStatus.pending;
                needsUpdate = true;
            }
        }
        if (needsUpdate)
        {
            await UpdateDocumentUrlStatusesAsync(collection, currentDoc.Id, currentDoc.UrlStatuses);
        }
    }

    var docImageCount = 0;
    var docSkippedCount = 0;
    var docFailedCount = 0;
    
    Console.WriteLine($"[{processedDocuments}/{totalDocuments}] Processing: '{currentDoc.Title}' ({currentDoc.Jpg6Urls.Count} URLs)");

    foreach (var url in currentDoc.Jpg6Urls)
    {
        if (string.IsNullOrWhiteSpace(url))
            continue;

        // Check if URL already processed
        var urlStatus = currentDoc.UrlStatuses.FirstOrDefault(u => u.Url == url);
        if (urlStatus == null)
        {
            urlStatus = new Jpg6UrlStatusInfo { Url = url, Status = Jpg6UrlStatus.pending };
            currentDoc.UrlStatuses.Add(urlStatus);
        }

        // Skip if already completed or failed (resumable)
        if (urlStatus.Status == Jpg6UrlStatus.completed || urlStatus.Status == Jpg6UrlStatus.failed)
        {
            Console.WriteLine($"  ⏭️  Skipping already processed URL: {url} (Status: {urlStatus.Status})");
            if (urlStatus.Status == Jpg6UrlStatus.completed)
                docImageCount += urlStatus.ImagesDownloaded ?? 0;
            else
                docFailedCount++;
            continue;
        }

        // Mark URL as processing
        urlStatus.Status = Jpg6UrlStatus.processing;
        await UpdateUrlStatusAsync(collection, currentDoc.Id, url, urlStatus);

        try
        {
            processedUrls++;
            Console.WriteLine($"  [{processedUrls}/{totalUrls}] Fetching: {url}");
            
            var images = await ExtractImagesFromUrlAsync(url, httpClient, retryPolicy, browser);

            if (images.Count == 0)
            {
                Console.WriteLine($"    ⚠️  No images found - skipping URL");
                urlStatus.Status = Jpg6UrlStatus.skipped;
                urlStatus.ImagesFound = 0;
                urlStatus.ImagesDownloaded = 0;
                urlStatus.ProcessedAt = DateTime.UtcNow;
                urlStatus.Error = "No images found";
                await UpdateUrlStatusAsync(collection, currentDoc.Id, url, urlStatus);
                
                errorUrls++;
                docFailedCount++;
                continue;
            }

            Console.WriteLine($"    Found {images.Count} image(s)");
            urlStatus.ImagesFound = images.Count;

            var urlDownloadedCount = 0;
            var urlSkippedCount = 0;

            foreach (var imageInfo in images)
            {
                Console.WriteLine($"    → Processing image: {imageInfo.Url} ({imageInfo.Width}x{imageInfo.Height})");
                
                // Skip if we've already processed this image URL
                if (processedImageUrls.Contains(imageInfo.Url))
                {
                    skippedImages++;
                    docSkippedCount++;
                    urlSkippedCount++;
                    Console.WriteLine($"      ⏭️  Skipped (duplicate)");
                    continue;
                }

                processedImageUrls.Add(imageInfo.Url);

                if (imageInfo.Width >= minSize || imageInfo.Height >= minSize)
                {
                    Console.WriteLine($"      ✓ Size OK ({imageInfo.Width}x{imageInfo.Height} >= {minSize}px), downloading...");
                    var saved = await SaveImageAsync(imageInfo, outputFolder, currentDoc.Title);
                    if (saved)
                    {
                        downloadedImages++;
                        docImageCount++;
                        urlDownloadedCount++;
                        Console.WriteLine($"      ✅ Downloaded: {Path.GetFileName(new Uri(imageInfo.Url).LocalPath)}");
                    }
                    else
                    {
                        skippedImages++;
                        docSkippedCount++;
                        urlSkippedCount++;
                        Console.WriteLine($"      ⏭️  Skipped (file already exists)");
                    }
                }
                else
                {
                    skippedImages++;
                    docSkippedCount++;
                    urlSkippedCount++;
                    Console.WriteLine($"      ⏭️  Skipped (too small: {imageInfo.Width}x{imageInfo.Height} < {minSize}px)");
                }
            }
            
            if (urlDownloadedCount > 0)
            {
                Console.WriteLine($"    ✓ Downloaded {urlDownloadedCount} image(s) from this URL");
            }
            else if (images.Count > 0)
            {
                Console.WriteLine($"    ⚠️  Found {images.Count} image(s) but none were downloaded (all skipped)");
            }

            // Update URL status to completed
            urlStatus.Status = Jpg6UrlStatus.completed;
            urlStatus.ImagesDownloaded = urlDownloadedCount;
            urlStatus.ProcessedAt = DateTime.UtcNow;
            urlStatus.Error = null;
            await UpdateUrlStatusAsync(collection, currentDoc.Id, url, urlStatus);

            // Progress update every 10 URLs or at document completion
            if (processedUrls % 10 == 0 || processedUrls == totalUrls)
            {
                Console.WriteLine($"    📊 Progress: {processedUrls}/{totalUrls} URLs | {downloadedImages} downloaded | {skippedImages} skipped | {errorUrls} errors");
            }
        }
        catch (Exception ex)
        {
            errorUrls++;
            docFailedCount++;
            
            // Mark URL as failed
            urlStatus.Status = Jpg6UrlStatus.failed;
            urlStatus.ProcessedAt = DateTime.UtcNow;
            urlStatus.Error = ex.Message;
            await UpdateUrlStatusAsync(collection, currentDoc.Id, url, urlStatus);
            
            Console.WriteLine($"    ❌ Error: {ex.Message}");
        }
    }
    
    // Mark document as processed (all URLs done, even if some failed)
    await MarkDocumentProcessedAsync(collection, currentDoc.Id);
    
    Console.WriteLine($"  ✓ Completed '{currentDoc.Title}': {docImageCount} images downloaded, {docSkippedCount} skipped, {docFailedCount} failed");
    Console.WriteLine();
}

Console.WriteLine();
Console.WriteLine("=== Download Summary ===");
Console.WriteLine($"Total URLs processed: {processedUrls}");
Console.WriteLine($"Images downloaded: {downloadedImages}");
Console.WriteLine($"Images skipped (too small or errors): {skippedImages}");
Console.WriteLine($"URL errors: {errorUrls}");
Console.WriteLine($"Output folder: {outputFolder}");

// Cleanup Playwright
if (browser != null)
{
    await browser.CloseAsync();
}
if (playwright != null)
{
    playwright.Dispose();
}

async Task<List<ImageInfo>> ExtractImagesFromUrlAsync(string url, HttpClient httpClient, AsyncRetryPolicy retryPolicy, IBrowser? browser)
{
    var images = new List<ImageInfo>();
    
    // Define debug log path at function level for all logging blocks
    var debugLogPath = @"c:\Users\ASUS\Downloads\Facial_Recognition\FaceSearch\.cursor\debug.log";
    var debugLogDir = Path.GetDirectoryName(debugLogPath);
    if (!string.IsNullOrEmpty(debugLogDir) && !Directory.Exists(debugLogDir))
    {
        Directory.CreateDirectory(debugLogDir);
    }

    // #region agent log
    try
    {
        var logEntry0 = System.Text.Json.JsonSerializer.Serialize(new {
            sessionId = "debug-session",
            runId = "run1",
            hypothesisId = "ALL",
            location = "Program.cs:324",
            message = "ExtractImagesFromUrlAsync called",
            data = new { url, hasBrowser = browser != null, isJpg6Su = url.Contains("jpg6.su"), timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
        });
        Console.WriteLine($"[DEBUG] {logEntry0}"); // Also log to console
        await File.AppendAllTextAsync(debugLogPath, logEntry0 + "\n");
    }
    catch (Exception logEx)
    {
        Console.WriteLine($"[DEBUG LOG ERROR] {logEx.Message}"); // Log errors to console
    }
    // #endregion

    try
    {
        // Track the final URL after redirects
        string finalUrl = url;
        
        // PRIORITY 1: For jpg6.su URLs, follow redirects to get the final URL
        // The redirect chain: https://jpg6.su/img/<ID> -> https://jpg6.su/img/<FILENAME>.<ID> -> https://simp3.selti-delivery.ru/images/<FILENAME><hash>.jpg
        // The final URL after all redirects is the actual image file
        if (url.Contains("jpg6.su"))
        {
            Console.WriteLine($"    -> Following redirects for jpg6.su URL: {url}");
            var redirectedUrl = await FollowRedirectsAndGetFinalUrlAsync(url, httpClient, retryPolicy);
            if (!string.IsNullOrEmpty(redirectedUrl) && redirectedUrl != url)
            {
                finalUrl = redirectedUrl;
                Console.WriteLine($"    -> Final URL after redirects: {finalUrl}");
                
                // Check if the final URL is a direct image
                var isImage = await TryAsDirectImageAsync(finalUrl, httpClient, retryPolicy, images);
                if (isImage && images.Count > 0)
                {
                    Console.WriteLine($"    *** SUCCESS - Final redirect URL is a direct image: {finalUrl}");
                    return images;
                }
                else
                {
                    Console.WriteLine($"    -> Final redirect URL is not a direct image, will try HTML parsing");
                    // Continue to HTML parsing below with the final URL
                    url = finalUrl;
                }
            }
        }
        
        // First, try to fetch as direct image
        var isDirectImage = await TryAsDirectImageAsync(url, httpClient, retryPolicy, images);
        if (isDirectImage)
        {
            // #region agent log
            try
            {
                var logEntryDirect = System.Text.Json.JsonSerializer.Serialize(new {
                    sessionId = "debug-session",
                    runId = "run1",
                    hypothesisId = "ALL",
                    location = "Program.cs:345",
                    message = "URL is direct image, returning",
                    data = new { url, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
                });
                await File.AppendAllTextAsync(debugLogPath, logEntryDirect + "\n");
            }
            catch { }
            // #endregion
            return images;
        }

        // If not a direct image, fetch as HTML and parse
        // Get the final URL after redirects (may have been updated by redirect following above)
        string htmlContent;
        
        // For jpg6.su pages, use Playwright to simulate human behavior:
        // 1. Navigate to URL (follows redirects automatically)
        // 2. Wait 2 seconds for page to fully load
        // 3. Find the center image
        // 4. Get its URL and download it
        if (url.Contains("jpg6.su") && browser != null)
        {
            Console.WriteLine($"    -> Using Playwright to find center image (human-like behavior)");
            
            htmlContent = await retryPolicy.ExecuteAsync(async () =>
            {
                await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                    ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
                    JavaScriptEnabled = true
                });
                
                // Add stealth script to hide webdriver and other automation indicators
                await context.AddInitScriptAsync(@"
                    Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
                    window.chrome = { runtime: {} };
                    Object.defineProperty(navigator, 'plugins', { get: () => [1, 2, 3, 4, 5] });
                    Object.defineProperty(navigator, 'languages', { get: () => ['en-US', 'en'] });
                ");
                
                var page = await context.NewPageAsync();
                
                try
                {
                    // Navigate to URL - Playwright automatically follows redirects
                    await page.GotoAsync(url, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.NetworkIdle,
                        Timeout = 30000 // 30 second timeout
                    });
                    
                    // Get the final URL after redirects
                    finalUrl = page.Url;
                    Console.WriteLine($"    -> Page loaded, final URL after redirects: {finalUrl}");
                    
                    // Wait 2 seconds for page to fully load (as a human would wait)
                    await page.WaitForTimeoutAsync(2000);
                    
                    // For debugging: Save HTML to test file for specific URL only
                    if (url.Contains("279441461-282545000753146-4605147622215035519-n-000467.WsjJzy") || 
                        finalUrl.Contains("279441461-282545000753146-4605147622215035519-n-000467.WsjJzy"))
                    {
                        try
                        {
                            var htmlContent = await page.ContentAsync();
                            var testFilePath = Path.Combine(outputFolder, "test_html_output.html");
                            Directory.CreateDirectory(outputFolder); // Ensure directory exists
                            await File.WriteAllTextAsync(testFilePath, htmlContent);
                            Console.WriteLine($"    -> Saved HTML to test file: {testFilePath}");
                            Console.WriteLine($"    -> File size: {new FileInfo(testFilePath).Length} bytes");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"    -> Error saving HTML file: {ex.Message}");
                            Console.WriteLine($"    -> Stack trace: {ex.StackTrace}");
                        }
                    }
                    
                    // Search the DOM for any URLs ending with image extensions
                    var imageUrls = await page.EvaluateAsync<string[]>(@"() => {
                        const imageExtensions = ['.jpg', '.jpeg', '.png', '.gif', '.webp', '.bmp', '.svg'];
                        const foundUrls = new Set();
                        
                        // Search all elements for URLs with image extensions
                        const allElements = document.querySelectorAll('*');
                        
                        for (const element of allElements) {
                            // Check src attributes
                            const src = element.getAttribute('src') || element.getAttribute('data-src') || element.getAttribute('data-lazy-src');
                            if (src) {
                                const srcLower = src.toLowerCase();
                                if (imageExtensions.some(ext => srcLower.endsWith(ext))) {
                                    foundUrls.add(src);
                                }
                            }
                            
                            // Check href attributes
                            const href = element.getAttribute('href');
                            if (href) {
                                const hrefLower = href.toLowerCase();
                                if (imageExtensions.some(ext => hrefLower.endsWith(ext))) {
                                    foundUrls.add(href);
                                }
                            }
                            
                            // Check text content for URLs
                            const text = element.textContent || '';
                            const urlRegex = /https?:\/\/[^\s""'<>]+\.(jpg|jpeg|png|gif|webp|bmp|svg)(\?[^\s""'<>]*)?/gi;
                            const matches = text.match(urlRegex);
                            if (matches) {
                                matches.forEach(match => foundUrls.add(match));
                            }
                        }
                        
                        // Also check the page HTML content
                        const htmlContent = document.documentElement.outerHTML;
                        const htmlMatches = htmlContent.match(/https?:\/\/[^\s""'<>]+\.(jpg|jpeg|png|gif|webp|bmp|svg)(\?[^\s""'<>]*)?/gi);
                        if (htmlMatches) {
                            htmlMatches.forEach(match => foundUrls.add(match));
                        }
                        
                        // Filter out thumbnails and return as array
                        const filteredUrls = Array.from(foundUrls).filter(url => {
                            const urlLower = url.toLowerCase();
                            return !urlLower.includes('.th.') && 
                                   !urlLower.includes('thumb') && 
                                   !urlLower.includes('_small') &&
                                   !urlLower.includes('_thumb') &&
                                   !urlLower.includes('logo') &&
                                   !urlLower.includes('favicon');
                        });
                        
                        // Prioritize URLs from simp1/simp2/simp3.selti-delivery.ru
                        return filteredUrls.sort((a, b) => {
                            const aLower = a.toLowerCase();
                            const bLower = b.toLowerCase();
                            
                            const aIsSimp = aLower.includes('simp1.selti-delivery.ru') || 
                                          aLower.includes('simp2.selti-delivery.ru') || 
                                          aLower.includes('simp3.selti-delivery.ru');
                            const bIsSimp = bLower.includes('simp1.selti-delivery.ru') || 
                                          bLower.includes('simp2.selti-delivery.ru') || 
                                          bLower.includes('simp3.selti-delivery.ru');
                            
                            if (aIsSimp && !bIsSimp) return -1;
                            if (!aIsSimp && bIsSimp) return 1;
                            
                            // Prioritize full-size indicators
                            const aHasFull = aLower.includes('full') || aLower.includes('large') || aLower.includes('original') || /\d+x\d+/.test(aLower);
                            const bHasFull = bLower.includes('full') || bLower.includes('large') || bLower.includes('original') || /\d+x\d+/.test(bLower);
                            
                            if (aHasFull && !bHasFull) return -1;
                            if (!aHasFull && bHasFull) return 1;
                            
                            return 0;
                        });
                    }");
                    
                    if (imageUrls != null && imageUrls.Length > 0)
                    {
                        Console.WriteLine($"    -> Found {imageUrls.Length} image URL(s) in DOM");
                        
                        // Try each URL in priority order
                        foreach (var imageUrl in imageUrls)
                        {
                            try
                            {
                                // Make URL absolute if it's relative
                                var absoluteUrl = imageUrl;
                                if (!Uri.IsWellFormedUriString(imageUrl, UriKind.Absolute))
                                {
                                    if (Uri.TryCreate(new Uri(finalUrl), imageUrl, out var absoluteUri))
                                    {
                                        absoluteUrl = absoluteUri.AbsoluteUri;
                                    }
                                    else
                                    {
                                        Console.WriteLine($"    -> Skipping invalid URL: {imageUrl}");
                                        continue;
                                    }
                                }
                                
                                Console.WriteLine($"    -> Trying image URL from DOM: {absoluteUrl}");
                                var foundImage = await TryAsDirectImageAsync(absoluteUrl, httpClient, retryPolicy, images);
                                if (foundImage && images.Count > 0)
                                {
                                    Console.WriteLine($"    *** SUCCESS - Downloaded image from DOM: {absoluteUrl}");
                                    return await page.ContentAsync(); // Return content to exit early
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"    -> Error trying DOM URL: {ex.Message}");
                            }
                        }
                        
                        Console.WriteLine($"    -> None of the {imageUrls.Length} DOM URLs worked, will try HTML parsing");
                    }
                    else
                    {
                        Console.WriteLine($"    -> No image URLs found in DOM, will try HTML parsing");
                    }
                    
                    // If DOM search didn't work, get page content for HTML parsing fallback
                    return await page.ContentAsync();
                }
                catch (TimeoutException)
                {
                    // If timeout, still try to get the content
                    Console.WriteLine($"    ⚠️  Page load timeout, attempting to extract content anyway");
                    finalUrl = page.Url;
                    return await page.ContentAsync();
                }
                finally
                {
                    await page.CloseAsync();
                }
            });
        }
        else
        {
            // For non-jpg6.su URLs or when Playwright is not available, use HttpClient
            htmlContent = await retryPolicy.ExecuteAsync(async () =>
            {
                // Create a request message to track redirects
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead);
                response.EnsureSuccessStatusCode();
                
                // Get the final URL after redirects (request.RequestUri is updated after redirects)
                finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? url;
                Console.WriteLine($"    -> Final URL after redirects (HttpClient): {finalUrl}");
                
                // Read HTML content
                return await response.Content.ReadAsStringAsync();
            });
        }
        
        // Check if HTML content indicates page doesn't exist (before parsing)
        if (string.IsNullOrWhiteSpace(htmlContent) || 
            htmlContent.Contains("That page doesn't exist", StringComparison.OrdinalIgnoreCase) ||
            htmlContent.Contains("page doesn't exist", StringComparison.OrdinalIgnoreCase) ||
            (htmlContent.Contains("404", StringComparison.OrdinalIgnoreCase) && 
             (htmlContent.Contains("not found", StringComparison.OrdinalIgnoreCase) || 
              htmlContent.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase))))
        {
            Console.WriteLine($"    ⏭️  Page doesn't exist, skipping: {finalUrl}");
            return images; // Return empty list
        }

        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(htmlContent);

        // Use the final URL after redirects as the base URI
        var baseUri = new Uri(finalUrl);
        
        Console.WriteLine($"    → Final URL after redirects: {finalUrl}");
        
        // Special handling for jpg6.su pages - look for direct image links
        if (url.Contains("jpg6.su"))
        {
            // Method 0: FIRST PRIORITY - Get ALL input fields and check their values
            // jpg6.su pages put the actual image URL in input field values
            var allInputs = htmlDoc.DocumentNode.SelectNodes("//input[@value]");
            if (allInputs != null && allInputs.Count > 0)
            {
                Console.WriteLine($"    → Found {allInputs.Count} input field(s) on page");
                var imageInputs = new List<(string url, int priority)>();
                
                foreach (var input in allInputs)
                {
                    var value = input.GetAttributeValue("value", null);
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    
                    var valuePreview = value.Length > 100 ? value.Substring(0, 100) + "..." : value;
                    Console.WriteLine($"      Checking input: {valuePreview}");
                    
                    // Must be a full URL starting with http
                    if (!value.StartsWith("http", StringComparison.OrdinalIgnoreCase)) 
                    {
                        Console.WriteLine($"        ⏭️  Not a full URL, skipping");
                        continue;
                    }
                    
                    // Must look like an image URL
                    var valueLower = value.ToLowerInvariant();
                    if (!valueLower.Contains(".jpg") && !valueLower.Contains(".png") && 
                        !valueLower.Contains(".jpeg") && !valueLower.Contains(".gif") && 
                        !valueLower.Contains(".webp") && !valueLower.Contains(".bmp"))
                    {
                        Console.WriteLine($"        ⏭️  Not an image URL, skipping");
                        continue;
                    }
                    
                    // Skip thumbnails
                    if (valueLower.Contains(".th.") || valueLower.Contains("thumb") || 
                        valueLower.Contains("_small") || valueLower.Contains("_thumb"))
                    {
                        Console.WriteLine($"        ⏭️  Thumbnail, skipping");
                        continue;
                    }
                    
                    // Prioritize full-size images
                    int priority = 1;
                    if (valueLower.Contains("1800x1800") || valueLower.Contains("full") || 
                        valueLower.Contains("large") || valueLower.Contains("original"))
                        priority = 3;
                    else if (System.Text.RegularExpressions.Regex.IsMatch(valueLower, @"\d+x\d+"))
                        priority = 2;
                    
                    imageInputs.Add((value, priority));
                    Console.WriteLine($"        ✓ Added image URL (priority {priority}): {value}");
                }
                
                // Try highest priority URLs first
                foreach (var (imageUrlValue, priority) in imageInputs.OrderByDescending(x => x.priority))
                {
                    try
                    {
                        Console.WriteLine($"    → Trying input field URL (priority {priority}): {imageUrlValue}");
                        var foundImage = await TryAsDirectImageAsync(imageUrlValue, httpClient, retryPolicy, images);
                        if (foundImage && images.Count > 0)
                        {
                            Console.WriteLine($"    ✓✓✓ SUCCESS - Found image from input field: {imageUrlValue}");
                            return images;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"    ⚠️  Error trying input URL: {ex.Message}");
                    }
                }
            }
            
            // Method 1: Look for the main image element, prioritizing full-size images over thumbnails
            var allImageNodes = htmlDoc.DocumentNode.SelectNodes("//img[@src] | //img[@data-src] | //img[@data-lazy-src]");
            HtmlNode? mainImage = null;
            
            if (allImageNodes != null && allImageNodes.Count > 0)
            {
                // Filter out thumbnails and small images
                var candidateImages = new List<(HtmlNode node, string src)>();
                foreach (var imgNode in allImageNodes)
                {
                    var src = imgNode.GetAttributeValue("src", null) ?? 
                             imgNode.GetAttributeValue("data-src", null) ??
                             imgNode.GetAttributeValue("data-lazy-src", null);
                    
                    if (!string.IsNullOrWhiteSpace(src))
                    {
                        // Skip thumbnails (common patterns: .th.jpg, .thumb, thumbnail, _thumb, _small)
                        var srcLower = src.ToLowerInvariant();
                        if (!srcLower.Contains(".th.") && 
                            !srcLower.Contains("thumb") && 
                            !srcLower.Contains("_small") &&
                            !srcLower.Contains("_thumb"))
                        {
                            candidateImages.Add((imgNode, src));
                        }
                    }
                }
                
                // Prefer images with larger dimensions in class/id or URL
                mainImage = candidateImages
                    .OrderByDescending(x => 
                    {
                        var src = x.src.ToLowerInvariant();
                        // Prioritize images with dimension indicators (e.g., 1800x1800, full, large)
                        if (System.Text.RegularExpressions.Regex.IsMatch(src, @"\d+x\d+")) return 3;
                        if (src.Contains("full") || src.Contains("large") || src.Contains("original")) return 2;
                        return 1;
                    })
                    .FirstOrDefault().node;
            }
            
            if (mainImage != null)
            {
                var imgSrc = mainImage.GetAttributeValue("src", null) ?? 
                            mainImage.GetAttributeValue("data-src", null) ??
                            mainImage.GetAttributeValue("data-lazy-src", null);
                if (!string.IsNullOrWhiteSpace(imgSrc))
                {
                    try
                    {
                        var imageUrl = new Uri(baseUri, imgSrc).AbsoluteUri;
                        var foundImage = await TryAsDirectImageAsync(imageUrl, httpClient, retryPolicy, images);
                        if (foundImage && images.Count > 0) return images;
                    }
                    catch { }
                }
            }
            
            // Method 2: Look for "Image URL" or "Full image" in the Direct links section
            // jpg6.su pages have sections with "Image URL" that contain the direct link
            var directLinksSection = htmlDoc.DocumentNode.SelectSingleNode("//div[contains(@class, 'direct-links') or contains(text(), 'Direct links')]");
            if (directLinksSection != null)
            {
                // Look for links or text containing image URLs
                var imageLinks = directLinksSection.SelectNodes(".//a[@href]");
                if (imageLinks != null)
                {
                    foreach (var link in imageLinks)
                    {
                        var href = link.GetAttributeValue("href", null);
                        if (!string.IsNullOrWhiteSpace(href) && 
                            (href.Contains(".jpg") || href.Contains(".png") || href.Contains(".jpeg") || 
                             href.Contains(".gif") || href.Contains(".webp") || href.Contains("jpg6.su")))
                        {
                            try
                            {
                                var imageUrl = new Uri(baseUri, href).AbsoluteUri;
                                var foundImage = await TryAsDirectImageAsync(imageUrl, httpClient, retryPolicy, images);
                                if (foundImage && images.Count > 0) return images;
                            }
                            catch { }
                        }
                    }
                }
            }
            
            // Method 3: Look for "Image URL" text and get the URL from nearby elements
            // jpg6.su pages have sections with "Image URL" that contain input fields with the direct URL
            var imageUrlSections = htmlDoc.DocumentNode.SelectNodes("//*[contains(text(), 'Image URL') or contains(text(), 'Full image') or contains(text(), 'Image link')]");
            if (imageUrlSections != null)
            {
                foreach (var section in imageUrlSections)
                {
                    // Look in the parent container for input fields
                    var container = section.ParentNode ?? section;
                    
                    // Find all input fields in the container
                    var inputs = container.SelectNodes(".//input[@value] | .//following-sibling::input[@value] | .//preceding-sibling::input[@value]");
                    if (inputs != null)
                    {
                        foreach (var input in inputs)
                        {
                            var value = input.GetAttributeValue("value", null);
                            if (!string.IsNullOrWhiteSpace(value) && 
                                (value.StartsWith("http") || value.Contains("jpg6.su") || value.Contains(".jpg") || value.Contains(".png")))
                            {
                                try
                                {
                                    var imageUrl = value.StartsWith("http") ? value : new Uri(baseUri, value).AbsoluteUri;
                                    var foundImage = await TryAsDirectImageAsync(imageUrl, httpClient, retryPolicy, images);
                                    if (foundImage && images.Count > 0) return images;
                                }
                                catch { }
                            }
                        }
                    }
                    
                    // Look for code/pre blocks that might contain the URL
                    var codeBlocks = container.SelectNodes(".//code | .//pre | .//following-sibling::code | .//following-sibling::pre");
                    if (codeBlocks != null)
                    {
                        foreach (var codeBlock in codeBlocks)
                        {
                            var codeText = codeBlock.InnerText;
                            var urlMatch = System.Text.RegularExpressions.Regex.Match(codeText, @"https?://[^\s""'<>]+");
                            if (urlMatch.Success)
                            {
                                try
                                {
                                    var foundImage = await TryAsDirectImageAsync(urlMatch.Value, httpClient, retryPolicy, images);
                                    if (foundImage && images.Count > 0) return images;
                                }
                                catch { }
                            }
                        }
                    }
                    
                    // Also check the text content of the section itself
                    var sectionText = container.InnerText;
                    var textUrlMatch = System.Text.RegularExpressions.Regex.Match(sectionText, @"https?://[^\s""'<>]+\.(jpg|jpeg|png|gif|webp|bmp)");
                    if (textUrlMatch.Success)
                    {
                        try
                        {
                            var foundImage = await TryAsDirectImageAsync(textUrlMatch.Value, httpClient, retryPolicy, images);
                            if (foundImage && images.Count > 0) return images;
                        }
                        catch { }
                    }
                }
            }
            
            
            // Method 4: Look for image URLs in meta tags (og:image, twitter:image)
            var ogImage = htmlDoc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
            if (ogImage != null)
            {
                var ogImageUrl = ogImage.GetAttributeValue("content", null);
                if (!string.IsNullOrWhiteSpace(ogImageUrl))
                {
                    try
                    {
                        var imageUrl = new Uri(baseUri, ogImageUrl).AbsoluteUri;
                        var foundImage = await TryAsDirectImageAsync(imageUrl, httpClient, retryPolicy, images);
                        if (foundImage && images.Count > 0) return images;
                    }
                    catch { }
                }
            }
            
            // Method 5: Extract all image URLs from the HTML content using regex
            // Prioritize full-size images over thumbnails
            var imageUrlPattern = new System.Text.RegularExpressions.Regex(@"https?://[^\s""'<>]+\.(jpg|jpeg|png|gif|webp|bmp)(\?[^\s""'<>]*)?", 
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var matches = imageUrlPattern.Matches(htmlContent);
            var imageCandidates = new List<(string url, int priority)>();
            var triedUrls = new HashSet<string>();
            
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                var imageUrl = match.Value;
                if (triedUrls.Contains(imageUrl)) continue;
                triedUrls.Add(imageUrl);
                
                var urlLower = imageUrl.ToLowerInvariant();
                // Skip thumbnails
                if (urlLower.Contains(".th.") || urlLower.Contains("thumb") || urlLower.Contains("_small") || urlLower.Contains("_thumb"))
                    continue;
                
                // Prioritize full-size images
                int priority = 1;
                if (urlLower.Contains("1800x1800") || urlLower.Contains("full") || urlLower.Contains("large") || urlLower.Contains("original"))
                    priority = 3;
                else if (System.Text.RegularExpressions.Regex.IsMatch(urlLower, @"\d+x\d+"))
                    priority = 2;
                
                if (imageUrl.Contains("jpg6.su") || 
                    imageUrl.Contains("simp1.selti-delivery.ru") || 
                    imageUrl.Contains("simp2.selti-delivery.ru"))
                {
                    imageCandidates.Add((imageUrl, priority));
                }
            }
            
            // Try highest priority URLs first
            foreach (var (imageUrl, _) in imageCandidates.OrderByDescending(x => x.priority))
            {
                try
                {
                    var foundImage = await TryAsDirectImageAsync(imageUrl, httpClient, retryPolicy, images);
                    if (foundImage && images.Count > 0) return images;
                }
                catch { }
            }
        }

        // Extract all img tags
        var imgNodes = htmlDoc.DocumentNode.SelectNodes("//img[@src]") ?? 
                      htmlDoc.DocumentNode.SelectNodes("//img[@data-src]") ??
                      htmlDoc.DocumentNode.SelectNodes("//img[@data-lazy-src]") ??
                      new HtmlNodeCollection(null);

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
                await TryAsDirectImageAsync(imageUrl, httpClient, retryPolicy, images);
            }
            catch
            {
                // Skip invalid URLs silently to reduce noise
            }
        }

        // Also check for picture/source tags
        var sourceNodes = htmlDoc.DocumentNode.SelectNodes("//source[@srcset]") ?? 
                         htmlDoc.DocumentNode.SelectNodes("//source[@src]") ??
                         new HtmlNodeCollection(null);

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
                    await TryAsDirectImageAsync(imageUrl, httpClient, retryPolicy, images);
                }
                catch { }
            }
        }
    }
    catch
    {
        // Error will be logged by caller
        throw;
    }

    return images;
}

async Task<string?> FollowRedirectsAndGetFinalUrlAsync(string url, HttpClient httpClient, AsyncRetryPolicy retryPolicy)
{
    try
    {
        // Create a temporary HttpClient that doesn't follow redirects automatically
        // so we can manually follow them and capture each step
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false
        };
        
        using var tempClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        tempClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        
        var currentUrl = url;
        var maxRedirects = 10;
        var redirectCount = 0;
        
        while (redirectCount < maxRedirects)
        {
            var req = new HttpRequestMessage(HttpMethod.Head, currentUrl);
            
            var resp = await retryPolicy.ExecuteAsync(async () =>
            {
                return await tempClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            });
            
            // Check if there's a redirect
            if (resp.StatusCode == HttpStatusCode.Redirect || 
                resp.StatusCode == HttpStatusCode.MovedPermanently ||
                resp.StatusCode == HttpStatusCode.Found ||
                resp.StatusCode == HttpStatusCode.SeeOther ||
                resp.StatusCode == HttpStatusCode.TemporaryRedirect ||
                resp.StatusCode == HttpStatusCode.PermanentRedirect)
            {
                // Get the Location header
                var location = resp.Headers.Location?.ToString();
                if (string.IsNullOrEmpty(location))
                {
                    // Try to get Location from response headers directly
                    if (resp.Headers.TryGetValues("Location", out var locationValues))
                    {
                        location = locationValues.FirstOrDefault();
                    }
                }
                
                if (!string.IsNullOrEmpty(location))
                {
                    // Make location absolute if it's relative
                    if (Uri.TryCreate(currentUrl, UriKind.Absolute, out var baseUri) &&
                        Uri.TryCreate(baseUri, location, out var absoluteUri))
                    {
                        currentUrl = absoluteUri.AbsoluteUri;
                        redirectCount++;
                        Console.WriteLine($"    -> Redirect {redirectCount}: {currentUrl}");
                        continue;
                    }
                    else if (Uri.TryCreate(location, UriKind.Absolute, out var absoluteLocation))
                    {
                        currentUrl = absoluteLocation.AbsoluteUri;
                        redirectCount++;
                        Console.WriteLine($"    -> Redirect {redirectCount}: {currentUrl}");
                        continue;
                    }
                }
            }
            
            // No more redirects, return the final URL
            return currentUrl;
        }
        
        // If we hit max redirects, return the last URL we got
        Console.WriteLine($"    -> Reached max redirects ({maxRedirects}), returning final URL: {currentUrl}");
        return currentUrl;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"    -> Error following redirects: {ex.Message}");
        return null;
    }
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
        // Sanitize title for folder name - preserve spaces, only remove invalid characters
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitizedTitle = new string(title.Where(c => !invalidChars.Contains(c)).ToArray());
        
        // Remove trailing periods and spaces (Windows doesn't allow these)
        sanitizedTitle = sanitizedTitle.TrimEnd('.', ' ');

        if (string.IsNullOrWhiteSpace(sanitizedTitle))
        {
            sanitizedTitle = "Untitled";
        }

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
            return false;
        }

        // Save image bytes (already downloaded during dimension check)
        if (imageInfo.ImageBytes != null && imageInfo.ImageBytes.Length > 0)
        {
            await File.WriteAllBytesAsync(filePath, imageInfo.ImageBytes);
            return true;
        }
        else
        {
            // Fallback: download again if bytes weren't stored
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30);
            var imageBytes = await httpClient.GetByteArrayAsync(imageInfo.Url);
            await File.WriteAllBytesAsync(filePath, imageBytes);
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

// MongoDB status update helpers
async Task MarkDocumentProcessingAsync(IMongoCollection<Jpg6DataMongo> collection, string documentId)
{
    var filter = Builders<Jpg6DataMongo>.Filter.Eq(x => x.Id, documentId);
    var update = Builders<Jpg6DataMongo>.Update
        .Set(x => x.Status, Jpg6DocumentStatus.processing);
    await collection.UpdateOneAsync(filter, update);
}

async Task MarkDocumentProcessedAsync(IMongoCollection<Jpg6DataMongo> collection, string documentId)
{
    var filter = Builders<Jpg6DataMongo>.Filter.Eq(x => x.Id, documentId);
    var update = Builders<Jpg6DataMongo>.Update
        .Set(x => x.Status, Jpg6DocumentStatus.processed)
        .Set(x => x.ProcessedAt, DateTime.UtcNow);
    await collection.UpdateOneAsync(filter, update);
}

async Task UpdateUrlStatusAsync(IMongoCollection<Jpg6DataMongo> collection, string documentId, string url, Jpg6UrlStatusInfo urlStatus)
{
    // Get current document
    var doc = await collection.Find(x => x.Id == documentId).FirstOrDefaultAsync();
    if (doc == null) return;

    // Initialize if needed
    if (doc.UrlStatuses == null)
        doc.UrlStatuses = new List<Jpg6UrlStatusInfo>();

    // Update or add URL status
    var existingIndex = doc.UrlStatuses.FindIndex(u => u.Url == url);
    if (existingIndex >= 0)
    {
        doc.UrlStatuses[existingIndex] = urlStatus;
    }
    else
    {
        doc.UrlStatuses.Add(urlStatus);
    }

    // Save back to MongoDB
    var filter = Builders<Jpg6DataMongo>.Filter.Eq(x => x.Id, documentId);
    var update = Builders<Jpg6DataMongo>.Update.Set(x => x.UrlStatuses, doc.UrlStatuses);
    await collection.UpdateOneAsync(filter, update);
}

async Task UpdateDocumentUrlStatusesAsync(IMongoCollection<Jpg6DataMongo> collection, string documentId, List<Jpg6UrlStatusInfo> urlStatuses)
{
    var filter = Builders<Jpg6DataMongo>.Filter.Eq(x => x.Id, documentId);
    var update = Builders<Jpg6DataMongo>.Update
        .Set(x => x.UrlStatuses, urlStatuses);
    await collection.UpdateOneAsync(filter, update);
}

class ImageInfo
{
    public string Url { get; set; } = default!;
    public int Width { get; set; }
    public int Height { get; set; }
    public string ContentType { get; set; } = "";
    public byte[]? ImageBytes { get; set; }
}


