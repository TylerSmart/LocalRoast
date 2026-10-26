namespace App.Core.Recipes;

public interface ITaxonomyService
{
    Task<IReadOnlyList<CollectionDto>> GetCollectionsAsync(
        CancellationToken cancellationToken = default
    );
    Task<CollectionDto> CreateCollectionAsync(
        UpsertCollectionRequest request,
        CancellationToken cancellationToken = default
    );
    Task<CollectionDto?> UpdateCollectionAsync(
        Guid id,
        UpsertCollectionRequest request,
        CancellationToken cancellationToken = default
    );
    Task<bool> DeleteCollectionAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TagDto>> GetTagsAsync(
        TagCategory? category = null,
        CancellationToken cancellationToken = default
    );
    Task<TagDto> CreateTagAsync(
        UpsertTagRequest request,
        CancellationToken cancellationToken = default
    );
    Task<TagDto?> UpdateTagAsync(
        Guid id,
        UpsertTagRequest request,
        CancellationToken cancellationToken = default
    );
    Task<bool> DeleteTagAsync(Guid id, CancellationToken cancellationToken = default);
}
