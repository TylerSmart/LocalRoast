using App.Core.Exceptions;
using App.Core.Shopping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace App.API.Controllers;

[ApiController]
[Route("shopping-lists")]
[Authorize]
public sealed class ShoppingListsController(IShoppingListService shoppingListService)
    : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ShoppingListSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLists(CancellationToken cancellationToken)
    {
        var lists = await shoppingListService.GetShoppingListsAsync(cancellationToken);
        return Ok(lists);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ShoppingListDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var list = await shoppingListService.GetShoppingListByIdAsync(id, cancellationToken);
        return list is not null ? Ok(list) : NotFound();
    }

    [HttpPost]
    [ProducesResponseType(typeof(ShoppingListDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        [FromBody] CreateShoppingListRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var list = await shoppingListService.CreateShoppingListAsync(
                request,
                cancellationToken
            );
            return CreatedAtAction(nameof(GetById), new { id = list.Id }, list);
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
    [ProducesResponseType(typeof(ShoppingListDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateShoppingListRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var list = await shoppingListService.UpdateShoppingListAsync(
                id,
                request,
                cancellationToken
            );
            return list is not null ? Ok(list) : NotFound();
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
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var deleted = await shoppingListService.DeleteShoppingListAsync(id, cancellationToken);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPost("{id:guid}/items")]
    [ProducesResponseType(typeof(ShoppingListItemDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddItem(
        Guid id,
        [FromBody] CreateShoppingListItemRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var item = await shoppingListService.AddItemAsync(id, request, cancellationToken);
            return item is not null
                ? CreatedAtAction(nameof(GetById), new { id }, item)
                : NotFound();
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
    }

    [HttpPut("{id:guid}/items/{itemId:guid}")]
    [ProducesResponseType(typeof(ShoppingListItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateItem(
        Guid id,
        Guid itemId,
        [FromBody] UpdateShoppingListItemRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var item = await shoppingListService.UpdateItemAsync(
                id,
                itemId,
                request,
                cancellationToken
            );
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
    }

    [HttpPatch("{id:guid}/items/{itemId:guid}/checked")]
    [ProducesResponseType(typeof(ShoppingListItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetItemChecked(
        Guid id,
        Guid itemId,
        [FromBody] SetShoppingListItemCheckedRequest request,
        CancellationToken cancellationToken
    )
    {
        var item = await shoppingListService.SetItemCheckedAsync(
            id,
            itemId,
            request,
            cancellationToken
        );
        return item is not null ? Ok(item) : NotFound();
    }

    [HttpPost("{id:guid}/items/{itemId:guid}/promote-staple")]
    [ProducesResponseType(typeof(ShoppingListItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PromotePantryStaple(
        Guid id,
        Guid itemId,
        CancellationToken cancellationToken
    )
    {
        var item = await shoppingListService.PromotePantryStapleItemAsync(
            id,
            itemId,
            cancellationToken
        );
        return item is not null ? Ok(item) : NotFound();
    }

    [HttpDelete("{id:guid}/items/{itemId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteItem(
        Guid id,
        Guid itemId,
        CancellationToken cancellationToken
    )
    {
        var deleted = await shoppingListService.DeleteItemAsync(id, itemId, cancellationToken);
        return deleted ? NoContent() : NotFound();
    }

    [HttpDelete("{id:guid}/items/checked")]
    [ProducesResponseType(typeof(ClearCheckedItemsResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ClearCheckedItems(Guid id, CancellationToken cancellationToken)
    {
        var result = await shoppingListService.ClearCheckedItemsAsync(id, cancellationToken);
        return result is not null ? Ok(result) : NotFound();
    }

    [HttpPost("{id:guid}/generate/meal-plan/preview")]
    [ProducesResponseType(typeof(ShoppingListGenerationPreviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PreviewGenerateFromMealPlan(
        Guid id,
        [FromBody] GenerateFromMealPlanRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var preview = await shoppingListService.PreviewGenerateFromMealPlanAsync(
                id,
                request,
                cancellationToken
            );
            return preview is not null ? Ok(preview) : NotFound();
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
    }

    [HttpPost("{id:guid}/generate/meal-plan")]
    [ProducesResponseType(typeof(ShoppingListGenerationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GenerateFromMealPlan(
        Guid id,
        [FromBody] GenerateFromMealPlanRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var result = await shoppingListService.GenerateFromMealPlanAsync(
                id,
                request,
                cancellationToken
            );
            return result is not null ? Ok(result) : NotFound();
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
    }

    [HttpPost("{id:guid}/generate/recipe")]
    [ProducesResponseType(typeof(ShoppingListGenerationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddRecipeIngredients(
        Guid id,
        [FromBody] AddRecipeToShoppingListRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var result = await shoppingListService.AddRecipeIngredientsAsync(
                id,
                request,
                cancellationToken
            );
            return result is not null ? Ok(result) : NotFound();
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
    }
}
