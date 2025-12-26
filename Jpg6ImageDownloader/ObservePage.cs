using Microsoft.Playwright;
using System;
using System.Threading.Tasks;

class ObservePage
{
    static async Task Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: Provide a jpg6.su URL to observe");
            Console.WriteLine("Example: dotnet run --project Jpg6ImageDownloader.csproj -- https://jpg6.su/img/YbQ5qE");
            return;
        }

        var url = args[0];
        Console.WriteLine($"Observing: {url}");
        Console.WriteLine("=" .PadRight(80, '='));

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = false // Show browser so we can see what's happening
        });

        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
        });

        var page = await context.NewPageAsync();

        // Log all network requests
        var networkRequests = new System.Collections.Generic.List<string>();
        page.Request += (sender, e) =>
        {
            var requestUrl = e.Url;
            if (requestUrl.Contains(".jpg") || requestUrl.Contains(".png") || requestUrl.Contains(".jpeg") ||
                requestUrl.Contains(".gif") || requestUrl.Contains(".webp"))
            {
                networkRequests.Add(requestUrl);
                Console.WriteLine($"[NETWORK REQUEST] {requestUrl}");
            }
        };

        // Log all responses
        page.Response += (sender, e) =>
        {
            var responseUrl = e.Url;
            var contentType = e.Headers.ContainsKey("content-type") ? e.Headers["content-type"] : "unknown";
            if (contentType.Contains("image"))
            {
                Console.WriteLine($"[NETWORK RESPONSE] {responseUrl} (Content-Type: {contentType})");
            }
        };

        try
        {
            Console.WriteLine("\n[1] Navigating to page...");
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 10000
            });

            Console.WriteLine($"    Final URL: {page.Url}");

            Console.WriteLine("\n[2] Waiting 3 seconds for JavaScript to execute...");
            await Task.Delay(3000);

            Console.WriteLine("\n[3] Getting all IMG elements:");
            var imgElements = await page.EvaluateAsync<string>(@"() => {
                const images = Array.from(document.querySelectorAll('img'));
                return JSON.stringify(images.map(img => ({
                    src: img.src,
                    dataSrc: img.getAttribute('data-src'),
                    dataLazySrc: img.getAttribute('data-lazy-src'),
                    currentSrc: img.currentSrc,
                    naturalWidth: img.naturalWidth,
                    naturalHeight: img.naturalHeight,
                    width: img.width,
                    height: img.height,
                    className: img.className,
                    id: img.id,
                    alt: img.alt
                })), null, 2);
            }");
            Console.WriteLine(imgElements);

            Console.WriteLine("\n[4] Getting all INPUT elements:");
            var inputElements = await page.EvaluateAsync<string>(@"() => {
                const inputs = Array.from(document.querySelectorAll('input'));
                return JSON.stringify(inputs.map(input => ({
                    type: input.type,
                    value: input.value ? (input.value.length > 200 ? input.value.substring(0, 200) + '...' : input.value) : null,
                    name: input.name,
                    id: input.id,
                    className: input.className,
                    placeholder: input.placeholder
                })), null, 2);
            }");
            Console.WriteLine(inputElements);

            Console.WriteLine("\n[5] Getting all elements with 'Image URL' or 'Full image' text:");
            var imageUrlSections = await page.EvaluateAsync<string>(@"() => {
                const walker = document.createTreeWalker(
                    document.body,
                    NodeFilter.SHOW_TEXT,
                    null,
                    false
                );
                const results = [];
                let node;
                while (node = walker.nextNode()) {
                    const text = node.textContent.trim();
                    if (text.includes('Image URL') || text.includes('Full image') || text.includes('Image link')) {
                        const parent = node.parentElement;
                        if (parent) {
                            results.push({
                                text: text,
                                parentTag: parent.tagName,
                                parentHtml: parent.outerHTML.substring(0, 500)
                            });
                        }
                    }
                }
                return JSON.stringify(results, null, 2);
            }");
            Console.WriteLine(imageUrlSections);

            Console.WriteLine("\n[6] Getting page HTML (first 5000 chars):");
            var html = await page.ContentAsync();
            Console.WriteLine(html.Substring(0, Math.Min(5000, html.Length)));

            Console.WriteLine("\n[7] Getting all links (A elements with href):");
            var links = await page.EvaluateAsync<string>(@"() => {
                const links = Array.from(document.querySelectorAll('a[href]'));
                return JSON.stringify(links.map(a => ({
                    href: a.href,
                    text: a.textContent.trim().substring(0, 100),
                    className: a.className
                })).filter(a => a.href.includes('.jpg') || a.href.includes('.png') || a.href.includes('jpg6.su') || a.href.includes('simp2.selti-delivery.ru')), null, 2);
            }");
            Console.WriteLine(links);

            Console.WriteLine("\n[8] Getting all meta tags:");
            var metaTags = await page.EvaluateAsync<string>(@"() => {
                const metas = Array.from(document.querySelectorAll('meta'));
                return JSON.stringify(metas.map(meta => ({
                    property: meta.getAttribute('property'),
                    name: meta.getAttribute('name'),
                    content: meta.getAttribute('content')
                })).filter(m => m.property || m.name), null, 2);
            }");
            Console.WriteLine(metaTags);

            Console.WriteLine("\n[9] Network requests captured:");
            foreach (var req in networkRequests)
            {
                Console.WriteLine($"    {req}");
            }

            Console.WriteLine("\n[10] Waiting 5 more seconds to see if anything loads...");
            await Task.Delay(5000);

            Console.WriteLine("\n[11] Getting IMG elements again (after wait):");
            var imgElementsAfter = await page.EvaluateAsync<string>(@"() => {
                const images = Array.from(document.querySelectorAll('img'));
                return JSON.stringify(images.map(img => ({
                    src: img.src,
                    dataSrc: img.getAttribute('data-src'),
                    currentSrc: img.currentSrc,
                    naturalWidth: img.naturalWidth,
                    naturalHeight: img.naturalHeight
                })), null, 2);
            }");
            Console.WriteLine(imgElementsAfter);

            Console.WriteLine("\n" + "=".PadRight(80, '='));
            Console.WriteLine("Observation complete. Browser will stay open for 10 seconds...");
            await Task.Delay(10000);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nERROR: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
        finally
        {
            await browser.CloseAsync();
        }
    }
}

