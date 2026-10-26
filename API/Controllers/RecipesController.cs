using App.Core.Exceptions;
using App.Core.Recipes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace App.API.Controllers;

[ApiController]
[Route("recipes")]
[Authorize]
public sealed class RecipesController(IRecipeService recipeService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<RecipeSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRecipes(
        [FromQuery] RecipeQueryParameters query,
        CancellationToken cancellationToken
    )
    {
        var recipes = await recipeService.GetRecipesAsync(query, cancellationToken);
        return Ok(recipes);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(RecipeDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRecipeById(Guid id, CancellationToken cancellationToken)
    {
        var recipe = await recipeService.GetRecipeByIdAsync(id, cancellationToken);
        return recipe is not null ? Ok(recipe) : NotFound();
    }

    [HttpPost]
    [ProducesResponseType(typeof(RecipeDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateRecipe(
        [FromBody] UpsertRecipeRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var recipe = await recipeService.CreateRecipeAsync(request, cancellationToken);
            return CreatedAtAction(nameof(GetRecipeById), new { id = recipe.Id }, recipe);
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
    [ProducesResponseType(typeof(RecipeDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateRecipe(
        Guid id,
        [FromBody] UpsertRecipeRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var recipe = await recipeService.UpdateRecipeAsync(id, request, cancellationToken);
            return recipe is not null ? Ok(recipe) : NotFound();
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
    public async Task<IActionResult> DeleteRecipe(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await recipeService.DeleteRecipeAsync(id, cancellationToken);
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

    [HttpPut("{id:guid}/organization")]
    [ProducesResponseType(typeof(RecipeDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateRecipeOrganization(
        Guid id,
        [FromBody] UpdateRecipeOrganizationRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var updated = await recipeService.UpdateRecipeOrganizationAsync(
                id,
                request,
                cancellationToken
            );
            return updated is not null ? Ok(updated) : NotFound();
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

    [HttpPost("match-ingredients")]
    [ProducesResponseType(typeof(IReadOnlyList<IngredientMatchResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> MatchRecipesByIngredients(
        [FromBody] IngredientMatchRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var results = await recipeService.MatchRecipesByIngredientsAsync(
                request,
                cancellationToken
            );
            return Ok(results);
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
