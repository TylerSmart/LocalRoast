using App.Core.Common;
using App.Core.Exceptions;
using App.Core.FoodCatalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace App.API.Controllers;

[ApiController]
[Route("food-items")]
[Authorize]
public sealed class FoodItemsController(IFoodCatalogService foodCatalogService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<FoodItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(IReadOnlyList<FoodItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFoodItems(
        [FromQuery] FoodItemQueryParameters query,
        CancellationToken cancellationToken
    )
    {
        if (query.Page.HasValue || query.PageSize.HasValue)
        {
            var paged = await foodCatalogService.GetFoodItemsPagedAsync(query, cancellationToken);
            return Ok(paged);
        }

        var items = await foodCatalogService.GetFoodItemsAsync(query, cancellationToken);
        return Ok(items);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(FoodItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetFoodItemById(Guid id, CancellationToken cancellationToken)
    {
        var item = await foodCatalogService.GetFoodItemByIdAsync(id, cancellationToken);
        return item is not null ? Ok(item) : NotFound();
    }

    [HttpPost]
    [ProducesResponseType(typeof(FoodItemDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateFoodItem(
        [FromBody] CreateFoodItemRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var item = await foodCatalogService.CreateFoodItemAsync(request, cancellationToken);
            return CreatedAtAction(nameof(GetFoodItemById), new { id = item.Id }, item);
        }
        catch (DomainValidationException ex)
        {
            return BadRequest(
                new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = ex.Title,
                    Detail = ex.Message,
                }
            );
        }
        catch (ConflictException ex)
        {
            return Conflict(
                new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = ex.Title,
                    Detail = ex.Message,
                }
            );
        }
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(FoodItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateFoodItem(
        Guid id,
        [FromBody] UpdateFoodItemRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var item = await foodCatalogService.UpdateFoodItemAsync(id, request, cancellationToken);
            return item is not null ? Ok(item) : NotFound();
        }
        catch (DomainValidationException ex)
        {
            return BadRequest(
                new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = ex.Title,
                    Detail = ex.Message,
                }
            );
        }
        catch (ConflictException ex)
        {
            return Conflict(
                new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = ex.Title,
                    Detail = ex.Message,
                }
            );
        }
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteFoodItem(
        Guid id,
        [FromQuery] bool unlinkFromRecipes = false,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var deleted = await foodCatalogService.DeleteFoodItemAsync(
                id,
                unlinkFromRecipes,
                cancellationToken
            );
            return deleted ? NoContent() : NotFound();
        }
        catch (ConflictException ex)
        {
            return Conflict(
                new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = ex.Title,
                    Detail = ex.Message,
                }
            );
        }
    }

    [HttpGet("{id:guid}/recipes")]
    [ProducesResponseType(typeof(PagedResultDto<FoodItemRecipeUsageDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(IReadOnlyList<FoodItemRecipeUsageDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetFoodItemRecipes(
        Guid id,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken
    )
    {
        try
        {
            if (page.HasValue || pageSize.HasValue)
            {
                var paged = await foodCatalogService.GetFoodItemRecipesPagedAsync(
                    id,
                    page ?? 1,
                    pageSize ?? 20,
                    cancellationToken
                );
                return Ok(paged);
            }

            var recipes = await foodCatalogService.GetFoodItemRecipesAsync(id, cancellationToken);
            return Ok(recipes);
        }
        catch (NotFoundException ex)
        {
            return NotFound(
                new ProblemDetails
                {
                    Status = StatusCodes.Status404NotFound,
                    Title = "Not Found",
                    Detail = ex.Message,
                }
            );
        }
    }

    [HttpPost("{id:guid}/unlink-recipes")]
    [ProducesResponseType(typeof(UnlinkFoodItemRecipesResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UnlinkFoodItemRecipes(
        Guid id,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var result = await foodCatalogService.UnlinkFoodItemRecipesAsync(id, cancellationToken);
            return Ok(result);
        }
        catch (NotFoundException ex)
        {
            return NotFound(
                new ProblemDetails
                {
                    Status = StatusCodes.Status404NotFound,
                    Title = "Not Found",
                    Detail = ex.Message,
                }
            );
        }
    }

    [HttpPost("merge")]
    [ProducesResponseType(typeof(MergeFoodItemsResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MergeFoodItems(
        [FromBody] MergeFoodItemsRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var result = await foodCatalogService.MergeFoodItemsAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (DomainValidationException ex)
        {
            return BadRequest(
                new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = ex.Title,
                    Detail = ex.Message,
                }
            );
        }
        catch (NotFoundException ex)
        {
            return NotFound(
                new ProblemDetails
                {
                    Status = StatusCodes.Status404NotFound,
                    Title = "Not Found",
                    Detail = ex.Message,
                }
            );
        }
    }
}
