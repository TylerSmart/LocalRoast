namespace App.Core.Authentication;

public interface IPersonalAccessTokenService
{
    Task<IReadOnlyList<PersonalAccessTokenDto>> GetTokensForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default
    );

    Task<CreatedPersonalAccessTokenDto> CreateTokenAsync(
        Guid userId,
        CreatePersonalAccessTokenRequest request,
        CancellationToken cancellationToken = default
    );

    Task<PersonalAccessTokenDto?> RevokeTokenAsync(
        Guid userId,
        Guid tokenId,
        CancellationToken cancellationToken = default
    );

    Task<bool> DeleteTokenAsync(
        Guid userId,
        Guid tokenId,
        CancellationToken cancellationToken = default
    );

    Task<ValidatedPersonalAccessTokenUser?> ValidateAndTouchTokenAsync(
        string plaintextToken,
        CancellationToken cancellationToken = default
    );
}
