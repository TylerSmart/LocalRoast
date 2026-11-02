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
    [ProducesResponseType(typeof(IReadOnlyList<FoodItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFoodItems(
        [FromQuery] FoodItemQueryParameters query,
        CancellationToken cancellationToken
    )
    {
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
    public async Task<IActionResult> DeleteFoodItem(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await foodCatalogService.DeleteFoodItemAsync(id, cancellationToken);
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
