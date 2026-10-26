using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using App.Core.Exceptions;
using App.Core.Ingestion;
using App.Core.Media;

namespace App.Infrastructure.Ingestion;

public sealed class WebRecipeScraper(
    HttpClient httpClient,
    IMediaStorageService mediaStorageService
) : IWebRecipeScraper
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private static readonly Regex ScriptJsonLdRegex = new(
        @"<script[^>]*type\s*=\s*[""']application\/ld\+json[""'][^>]*>(.*?)<\/script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled,
        RegexTimeout
    );

    private static readonly Regex TitleTagRegex = new(
        @"<title[^>]*>(.*?)<\/title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled,
        RegexTimeout
    );

    private static readonly Regex MetaTagRegex = new(
        @"<meta\s+[^>]*(?:name|property)\s*=\s*(?:""([^""]*)""|'([^']*)')[^>]*content\s*=\s*(?:""([^""]*)""|'([^']*)')[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout
    );

    private static readonly Regex MetaTagReverseRegex = new(
        @"<meta\s+[^>]*content\s*=\s*(?:""([^""]*)""|'([^']*)')[^>]*(?:name|property)\s*=\s*(?:""([^""]*)""|'([^']*)')[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout
    );

    private static readonly Regex HtmlTagsToStripRegex = new(
        @"<(?:script|style|noscript|svg|nav|header|footer|aside|form|iframe)[\s\S]*?<\/(?:script|style|noscript|svg|nav|header|footer|aside|form|iframe)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout
    );

    private static readonly Regex HtmlCommentsRegex = new(
        @"<!--[\s\S]*?-->",
        RegexOptions.Compiled,
        RegexTimeout
    );

    private static readonly Regex BlockTagsRegex = new(
        @"<\/(?:p|div|h[1-6]|li|tr|blockquote|section|article)>\s*|<(?:br|hr)\s*\/?>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout
    );

    private static readonly Regex AnyTagRegex = new(
        @"<[^>]+>",
        RegexOptions.Compiled,
        RegexTimeout
    );

    private static readonly Regex WhitespaceRegex = new(
        @"[ \t]+",
        RegexOptions.Compiled,
        RegexTimeout
    );

    private static readonly Regex MultiNewlineRegex = new(
        @"(\r?\n){3,}",
        RegexOptions.Compiled,
        RegexTimeout
    );

    public async Task<WebScrapeResult> ScrapeRecipePageAsync(
        string url,
        CancellationToken cancellationToken = default
    )
    {
        if (
            string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        )
        {
            throw new DomainValidationException(
                "Invalid URL. Must be an absolute http or https URL."
            );
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add(
            "User-Agent",
            "Mozilla/5.0 (compatible; LocalRoast/1.0; +https://localroast.local)"
        );
        request.Headers.Add(
            "Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"
        );

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );
        if (!response.IsSuccessStatusCode)
        {
            throw new DomainValidationException(
                $"Failed to fetch recipe URL: HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
            );
        }

        const int maxHtmlBytes = 5 * 1024 * 1024; // 5 MB
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memoryStream = new MemoryStream();

        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (memoryStream.Length + bytesRead > maxHtmlBytes)
            {
                break;
            }
            memoryStream.Write(buffer, 0, bytesRead);
        }

        memoryStream.Position = 0;
        using var reader = new StreamReader(memoryStream);
        var html = await reader.ReadToEndAsync(cancellationToken);

        // 1. Extract JSON-LD
        var jsonLdRecipe = FindRecipeJsonLd(html);

        // 2. Extract Fallback Metadata
        var metaTags = ExtractMetaTags(html);
        var pageTitle = ExtractPageTitle(html);

        var title = jsonLdRecipe?.Name ?? metaTags.GetValueOrDefault("og:title") ?? pageTitle;
        var siteName = metaTags.GetValueOrDefault("og:site_name") ?? uri.Host;
        var author =
            jsonLdRecipe?.Author
            ?? metaTags.GetValueOrDefault("author")
            ?? metaTags.GetValueOrDefault("article:author");
        var description =
            jsonLdRecipe?.Description
            ?? metaTags.GetValueOrDefault("og:description")
            ?? metaTags.GetValueOrDefault("description");
        var primaryImageUrl = jsonLdRecipe?.ImageUrl ?? metaTags.GetValueOrDefault("og:image");

        var prepTimeIso = jsonLdRecipe?.PrepTimeIso;
        var prepTimeMin = ParseDurationMinutes(prepTimeIso);

        var cookTimeIso = jsonLdRecipe?.CookTimeIso;
        var cookTimeMin = ParseDurationMinutes(cookTimeIso);

        var totalTimeIso = jsonLdRecipe?.TotalTimeIso;
        var totalTimeMin = ParseDurationMinutes(totalTimeIso);

        var rawIngredients = jsonLdRecipe?.Ingredients ?? [];
        var rawInstructions = jsonLdRecipe?.Instructions ?? [];

        // 3. Clean Readable DOM Text
        var cleanedText = CleanHtmlToText(html);

        var payload = new ScrapedWebRecipePayload(
            CanonicalUrl: uri.AbsoluteUri,
            PageTitle: title,
            SiteName: siteName,
            Author: author,
            Description: description,
            PrepTimeIso: prepTimeIso,
            PrepTimeMinutes: prepTimeMin,
            CookTimeIso: cookTimeIso,
            CookTimeMinutes: cookTimeMin,
            TotalTimeIso: totalTimeIso,
            TotalTimeMinutes: totalTimeMin,
            RecipeYieldText: jsonLdRecipe?.Yield,
            PrimaryImageUrl: primaryImageUrl,
            RawIngredientLines: rawIngredients,
            RawInstructionLines: rawInstructions,
            CleanedBodyText: cleanedText,
            HadRecipeJsonLd: jsonLdRecipe is not null
        );

        // 4. Download cover image locally if found
        string? coverPath = null;
        string? thumbPath = null;

        if (!string.IsNullOrWhiteSpace(primaryImageUrl))
        {
            try
            {
                var imageUri = Uri.TryCreate(
                    primaryImageUrl.Trim(),
                    UriKind.Absolute,
                    out var absUri
                )
                    ? absUri
                    : new Uri(uri, primaryImageUrl.Trim());

                using var imgCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    imgCts.Token
                );

                using var imgResponse = await httpClient.GetAsync(
                    imageUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    linkedCts.Token
                );
                if (imgResponse.IsSuccessStatusCode)
                {
                    const int maxImageBytes = 15 * 1024 * 1024;
                    var contentLength = imgResponse.Content.Headers.ContentLength ?? 0;
                    if (contentLength <= maxImageBytes)
                    {
                        var imgBytes = await imgResponse.Content.ReadAsByteArrayAsync(
                            linkedCts.Token
                        );
                        if (imgBytes.Length > 0 && imgBytes.Length <= maxImageBytes)
                        {
                            using var imgStream = new MemoryStream(imgBytes);
                            var fileName = Path.GetFileName(imageUri.LocalPath);
                            if (string.IsNullOrWhiteSpace(fileName))
                            {
                                fileName = "recipe-cover.jpg";
                            }
                            var savedCover = await mediaStorageService.SaveRecipeCoverAsync(
                                imgStream,
                                fileName,
                                imgBytes.Length,
                                linkedCts.Token
                            );
                            coverPath = savedCover.CoverImagePath;
                            thumbPath = savedCover.ThumbnailImagePath;
                        }
                    }
                }
            }
            catch
            {
                // Best-effort image download: do not fail scraping if cover image download fails
            }
        }

        return new WebScrapeResult(
            Payload: payload,
            DownloadedCoverImagePath: coverPath,
            DownloadedThumbnailImagePath: thumbPath
        );
    }

    private sealed class JsonLdRecipeExtract
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Author { get; set; }
        public string? ImageUrl { get; set; }
        public string? Yield { get; set; }
        public string? PrepTimeIso { get; set; }
        public string? CookTimeIso { get; set; }
        public string? TotalTimeIso { get; set; }
        public List<string> Ingredients { get; set; } = [];
        public List<string> Instructions { get; set; } = [];
    }

    private static JsonLdRecipeExtract? FindRecipeJsonLd(string html)
    {
        var matches = ScriptJsonLdRegex.Matches(html);
        foreach (Match match in matches)
        {
            var jsonText = match.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(jsonText))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(jsonText);
                var found = SearchJsonElementForRecipe(doc.RootElement);
                if (found is not null)
                {
                    return found;
                }
            }
            catch (JsonException)
            {
                // Continue to next script tag if JSON is malformed
            }
        }

        return null;
    }

    private static JsonLdRecipeExtract? SearchJsonElementForRecipe(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (IsRecipeType(element))
                {
                    return ParseRecipeObject(element);
                }

                if (
                    element.TryGetProperty("@graph", out var graph)
                    && graph.ValueKind == JsonValueKind.Array
                )
                {
                    foreach (var item in graph.EnumerateArray())
                    {
                        var res = SearchJsonElementForRecipe(item);
                        if (res is not null)
                        {
                            return res;
                        }
                    }
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var res = SearchJsonElementForRecipe(item);
                    if (res is not null)
                    {
                        return res;
                    }
                }
                break;
        }

        return null;
    }

    private static bool IsRecipeType(JsonElement element)
    {
        if (!element.TryGetProperty("@type", out var typeProp))
        {
            return false;
        }

        if (typeProp.ValueKind == JsonValueKind.String)
        {
            var str = typeProp.GetString();
            return str != null
                && (
                    str.Equals("Recipe", StringComparison.OrdinalIgnoreCase)
                    || str.EndsWith("/Recipe", StringComparison.OrdinalIgnoreCase)
                );
        }

        if (typeProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in typeProp.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var str = item.GetString();
                    if (
                        str != null
                        && (
                            str.Equals("Recipe", StringComparison.OrdinalIgnoreCase)
                            || str.EndsWith("/Recipe", StringComparison.OrdinalIgnoreCase)
                        )
                    )
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static JsonLdRecipeExtract ParseRecipeObject(JsonElement element)
    {
        var result = new JsonLdRecipeExtract();

        if (
            element.TryGetProperty("name", out var nameProp)
            && nameProp.ValueKind == JsonValueKind.String
        )
        {
            result.Name = WebUtility.HtmlDecode(nameProp.GetString());
        }

        if (
            element.TryGetProperty("description", out var descProp)
            && descProp.ValueKind == JsonValueKind.String
        )
        {
            result.Description = WebUtility.HtmlDecode(descProp.GetString());
        }

        // Author
        if (element.TryGetProperty("author", out var authorProp))
        {
            if (authorProp.ValueKind == JsonValueKind.String)
            {
                result.Author = WebUtility.HtmlDecode(authorProp.GetString());
            }
            else if (
                authorProp.ValueKind == JsonValueKind.Object
                && authorProp.TryGetProperty("name", out var an)
                && an.ValueKind == JsonValueKind.String
            )
            {
                result.Author = WebUtility.HtmlDecode(an.GetString());
            }
            else if (authorProp.ValueKind == JsonValueKind.Array)
            {
                var authors = new List<string>();
                foreach (var a in authorProp.EnumerateArray())
                {
                    if (a.ValueKind == JsonValueKind.String)
                    {
                        authors.Add(WebUtility.HtmlDecode(a.GetString())!);
                    }
                    else if (
                        a.ValueKind == JsonValueKind.Object
                        && a.TryGetProperty("name", out var n)
                        && n.ValueKind == JsonValueKind.String
                    )
                    {
                        authors.Add(WebUtility.HtmlDecode(n.GetString())!);
                    }
                }
                if (authors.Count > 0)
                {
                    result.Author = string.Join(", ", authors);
                }
            }
        }

        // Image
        if (element.TryGetProperty("image", out var imgProp))
        {
            if (imgProp.ValueKind == JsonValueKind.String)
            {
                result.ImageUrl = imgProp.GetString();
            }
            else if (
                imgProp.ValueKind == JsonValueKind.Object
                && imgProp.TryGetProperty("url", out var iu)
                && iu.ValueKind == JsonValueKind.String
            )
            {
                result.ImageUrl = iu.GetString();
            }
            else if (imgProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in imgProp.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        result.ImageUrl = item.GetString();
                        break;
                    }
                    if (
                        item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("url", out var u)
                        && u.ValueKind == JsonValueKind.String
                    )
                    {
                        result.ImageUrl = u.GetString();
                        break;
                    }
                }
            }
        }

        // Yield
        if (element.TryGetProperty("recipeYield", out var yieldProp))
        {
            if (yieldProp.ValueKind == JsonValueKind.String)
            {
                result.Yield = yieldProp.GetString();
            }
            else if (yieldProp.ValueKind == JsonValueKind.Number)
            {
                result.Yield = yieldProp.GetRawText();
            }
            else if (yieldProp.ValueKind == JsonValueKind.Array)
            {
                var yields = new List<string>();
                foreach (var y in yieldProp.EnumerateArray())
                {
                    if (y.ValueKind == JsonValueKind.String)
                        yields.Add(y.GetString()!);
                    else if (y.ValueKind == JsonValueKind.Number)
                        yields.Add(y.GetRawText());
                }
                if (yields.Count > 0)
                {
                    result.Yield = string.Join(", ", yields);
                }
            }
        }

        // Durations
        if (element.TryGetProperty("prepTime", out var pt) && pt.ValueKind == JsonValueKind.String)
            result.PrepTimeIso = pt.GetString();
        if (element.TryGetProperty("cookTime", out var ct) && ct.ValueKind == JsonValueKind.String)
            result.CookTimeIso = ct.GetString();
        if (element.TryGetProperty("totalTime", out var tt) && tt.ValueKind == JsonValueKind.String)
            result.TotalTimeIso = tt.GetString();

        // Ingredients
        if (
            element.TryGetProperty("recipeIngredient", out var ingProp)
            && ingProp.ValueKind == JsonValueKind.Array
        )
        {
            foreach (var ing in ingProp.EnumerateArray())
            {
                if (ing.ValueKind == JsonValueKind.String)
                {
                    var text = WebUtility.HtmlDecode(ing.GetString()?.Trim());
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        result.Ingredients.Add(text);
                    }
                }
            }
        }

        // Instructions
        if (element.TryGetProperty("recipeInstructions", out var instProp))
        {
            ExtractInstructions(instProp, result.Instructions);
        }

        return result;
    }

    private static void ExtractInstructions(JsonElement element, List<string> instructions)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var text = WebUtility.HtmlDecode(element.GetString()?.Trim());
            if (!string.IsNullOrWhiteSpace(text))
            {
                instructions.Add(text);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var text = WebUtility.HtmlDecode(item.GetString()?.Trim());
                    if (!string.IsNullOrWhiteSpace(text))
                        instructions.Add(text);
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    if (
                        item.TryGetProperty("@type", out var typeProp)
                        && typeProp.ValueKind == JsonValueKind.String
                        && typeProp
                            .GetString()
                            ?.Equals("HowToSection", StringComparison.OrdinalIgnoreCase) == true
                    )
                    {
                        var sectionName =
                            item.TryGetProperty("name", out var sn)
                            && sn.ValueKind == JsonValueKind.String
                                ? sn.GetString()?.Trim()
                                : null;

                        if (
                            item.TryGetProperty("itemListElement", out var subItems)
                            && subItems.ValueKind == JsonValueKind.Array
                        )
                        {
                            foreach (var sub in subItems.EnumerateArray())
                            {
                                var text = ExtractSingleStepText(sub);
                                if (!string.IsNullOrWhiteSpace(text))
                                {
                                    instructions.Add(
                                        !string.IsNullOrWhiteSpace(sectionName)
                                            ? $"[Section: {sectionName}] {text}"
                                            : text
                                    );
                                }
                            }
                        }
                    }
                    else
                    {
                        var text = ExtractSingleStepText(item);
                        if (!string.IsNullOrWhiteSpace(text))
                            instructions.Add(text);
                    }
                }
            }
        }
    }

    private static string? ExtractSingleStepText(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.String)
        {
            return WebUtility.HtmlDecode(item.GetString()?.Trim());
        }

        if (item.ValueKind == JsonValueKind.Object)
        {
            if (item.TryGetProperty("text", out var tp) && tp.ValueKind == JsonValueKind.String)
            {
                return WebUtility.HtmlDecode(tp.GetString()?.Trim());
            }
            if (item.TryGetProperty("name", out var np) && np.ValueKind == JsonValueKind.String)
            {
                return WebUtility.HtmlDecode(np.GetString()?.Trim());
            }
        }

        return null;
    }

    private static int? ParseDurationMinutes(string? isoDuration)
    {
        if (string.IsNullOrWhiteSpace(isoDuration))
        {
            return null;
        }

        try
        {
            var ts = XmlConvert.ToTimeSpan(isoDuration.Trim());
            return (int)Math.Round(ts.TotalMinutes);
        }
        catch
        {
            // Fallback regex for e.g. PT1H30M or PT45M
            var match = Regex.Match(
                isoDuration,
                @"PT(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?",
                RegexOptions.IgnoreCase
            );
            if (match.Success)
            {
                var h = match.Groups[1].Success ? int.Parse(match.Groups[1].Value) : 0;
                var m = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
                var s = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
                return (h * 60) + m + (s >= 30 ? 1 : 0);
            }
            return null;
        }
    }

    private static Dictionary<string, string> ExtractMetaTags(string html)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in MetaTagRegex.Matches(html))
        {
            var key = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            var val = match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value;

            key = key.Trim();
            val = WebUtility.HtmlDecode(val.Trim());

            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(val))
            {
                result.TryAdd(key, val);
            }
        }

        foreach (Match match in MetaTagReverseRegex.Matches(html))
        {
            var val = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            var key = match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value;

            key = key.Trim();
            val = WebUtility.HtmlDecode(val.Trim());

            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(val))
            {
                result.TryAdd(key, val);
            }
        }

        return result;
    }

    private static string? ExtractPageTitle(string html)
    {
        var match = TitleTagRegex.Match(html);
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value.Trim()) : null;
    }

    private static string CleanHtmlToText(string html)
    {
        // 1. Remove non-content tags
        var noScripts = HtmlTagsToStripRegex.Replace(html, " ");
        var noComments = HtmlCommentsRegex.Replace(noScripts, " ");

        // 2. Convert block endings to newlines
        var withNewlines = BlockTagsRegex.Replace(noComments, "\n");

        // 3. Strip all remaining tags
        var noTags = AnyTagRegex.Replace(withNewlines, " ");

        // 4. Decode HTML entities
        var decoded = WebUtility.HtmlDecode(noTags);

        // 5. Clean whitespace & newlines
        var singleSpaces = WhitespaceRegex.Replace(decoded, " ");
        var cleanedNewlines = MultiNewlineRegex.Replace(singleSpaces, "\n\n").Trim();

        // 6. Truncate to 24,000 characters
        if (cleanedNewlines.Length > 24000)
        {
            cleanedNewlines = cleanedNewlines[..24000];
        }

        return cleanedNewlines;
    }
}
