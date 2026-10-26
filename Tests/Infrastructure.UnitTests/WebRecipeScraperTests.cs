using System.Net;
using System.Text;
using App.Core.Exceptions;
using App.Core.Media;
using App.Infrastructure.Ingestion;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.Infrastructure.UnitTests;

[TestClass]
public sealed class WebRecipeScraperTests
{
    private sealed class MockHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handlerFunc
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult(handlerFunc(request));
        }
    }

    private sealed class MockMediaStorageService : IMediaStorageService
    {
        public Task<UploadedRecipeCoverDto> SaveRecipeCoverAsync(
            Stream content,
            string originalFileName,
            long contentLength,
            CancellationToken cancellationToken = default
        )
        {
            return Task.FromResult(
                new UploadedRecipeCoverDto(
                    "/api/uploads/recipes/test-cover.webp",
                    "/api/uploads/recipes/test-thumb.webp"
                )
            );
        }

        public Task<UploadedMediaDto> SaveStepImageAsync(
            Stream content,
            string originalFileName,
            long contentLength,
            CancellationToken cancellationToken = default
        )
        {
            return Task.FromResult(new UploadedMediaDto("/api/uploads/steps/test.webp"));
        }

        public Task DeleteMediaFileAsync(
            string? mediaUrlPath,
            CancellationToken cancellationToken = default
        )
        {
            return Task.CompletedTask;
        }
    }

    [TestMethod]
    public async Task ScrapeRecipePageAsync_InvalidUrlScheme_ThrowsDomainValidationException()
    {
        var scraper = new WebRecipeScraper(new HttpClient(), new MockMediaStorageService());

        await Assert.ThrowsAsync<DomainValidationException>(async () =>
        {
            await scraper.ScrapeRecipePageAsync("ftp://example.com/recipe");
        });
    }

    [TestMethod]
    public async Task ScrapeRecipePageAsync_WithJsonLdRecipe_ExtractsMetadataAndIngredients()
    {
        const string html = """
            <!DOCTYPE html>
            <html>
            <head>
                <title>Classic Guacamole Recipe</title>
                <script type="application/ld+json">
                {
                    "@context": "https://schema.org",
                    "@type": "Recipe",
                    "name": "Classic Fresh Guacamole",
                    "description": "Authentic Mexican guacamole with ripe avocados, lime, and cilantro.",
                    "author": { "@type": "Person", "name": "Abuela Maria" },
                    "prepTime": "PT15M",
                    "cookTime": "PT0M",
                    "totalTime": "PT15M",
                    "recipeYield": "4 servings",
                    "recipeIngredient": [
                        "3 ripe avocados, halved and pitted",
                        "1/2 small white onion, finely diced",
                        "2 tbsp fresh lime juice",
                        "1 tsp kosher salt"
                    ],
                    "recipeInstructions": [
                        { "@type": "HowToStep", "text": "Mash avocados with a fork in a molcajete." },
                        { "@type": "HowToStep", "text": "Fold in diced onion, lime juice, and salt." }
                    ]
                }
                </script>
            </head>
            <body>
                <nav><a href="/">Home</a></nav>
                <p>Enjoy with warm tortilla chips.</p>
            </body>
            </html>
            """;

        var handler = new MockHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html"),
        });

        var scraper = new WebRecipeScraper(new HttpClient(handler), new MockMediaStorageService());
        var result = await scraper.ScrapeRecipePageAsync("https://example.com/recipes/guacamole");

        Assert.IsTrue(result.Payload.HadRecipeJsonLd);
        Assert.AreEqual("Classic Fresh Guacamole", result.Payload.PageTitle);
        Assert.AreEqual("Abuela Maria", result.Payload.Author);
        Assert.AreEqual("4 servings", result.Payload.RecipeYieldText);
        Assert.AreEqual(15, result.Payload.PrepTimeMinutes);
        Assert.AreEqual(4, result.Payload.RawIngredientLines.Count);
        Assert.AreEqual(2, result.Payload.RawInstructionLines.Count);
        StringAssert.Contains(result.Payload.RawIngredientLines[0], "avocados");
    }

    [TestMethod]
    public async Task ScrapeRecipePageAsync_WithoutJsonLd_ExtractsMetaTagsAndCleanedBody()
    {
        const string html = """
            <!DOCTYPE html>
            <html>
            <head>
                <title>Grandma's Meatballs</title>
                <meta property="og:title" content="Grandma's Italian Meatballs">
                <meta property="og:site_name" content="Family Traditions">
                <meta name="author" content="Nonnas Kitchen">
                <meta property="og:description" content="Tender meatballs in slow-simmered marinara.">
            </head>
            <body>
                <header>Navigation header</header>
                <main>
                    <h1>Grandma's Italian Meatballs</h1>
                    <p>Combine ground beef, eggs, breadcrumbs, and parsley. Form into 2-inch balls and simmer.</p>
                </main>
                <footer>Copyright 2026</footer>
            </body>
            </html>
            """;

        var handler = new MockHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html"),
        });

        var scraper = new WebRecipeScraper(new HttpClient(handler), new MockMediaStorageService());
        var result = await scraper.ScrapeRecipePageAsync("https://example.com/meatballs");

        Assert.IsFalse(result.Payload.HadRecipeJsonLd);
        Assert.AreEqual("Grandma's Italian Meatballs", result.Payload.PageTitle);
        Assert.AreEqual("Family Traditions", result.Payload.SiteName);
        Assert.AreEqual("Nonnas Kitchen", result.Payload.Author);
        StringAssert.Contains(result.Payload.CleanedBodyText, "Combine ground beef");
        // Verify script/header/footer tags were stripped
        Assert.IsFalse(result.Payload.CleanedBodyText.Contains("Navigation header"));
        Assert.IsFalse(result.Payload.CleanedBodyText.Contains("Copyright 2026"));
    }
}
