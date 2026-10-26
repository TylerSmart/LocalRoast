namespace App.Core.Ingestion;

public interface IWebRecipeScraper
{
    Task<WebScrapeResult> ScrapeRecipePageAsync(
        string url,
        CancellationToken cancellationToken = default
    );
}
