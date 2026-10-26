namespace App.Core.Recipes;

public interface IShareLinkService
{
    Task<IReadOnlyList<ShareLinkDto>> GetShareLinksForRecipeAsync(
        Guid recipeId,
        CancellationToken cancellationToken = default
    );

    Task<ShareLinkDto> CreateShareLinkAsync(
        Guid recipeId,
        CreateShareLinkRequest request,
        CancellationToken cancellationToken = default
    );

    Task<ShareLinkDto?> RevokeShareLinkAsync(
        Guid recipeId,
        Guid shareLinkId,
        CancellationToken cancellationToken = default
    );

    Task<bool> DeleteShareLinkAsync(
        Guid recipeId,
        Guid shareLinkId,
        CancellationToken cancellationToken = default
    );

    Task<PublicSharedRecipeDto?> GetPublicSharedRecipeByTokenAsync(
        string token,
        CancellationToken cancellationToken = default
    );
}
