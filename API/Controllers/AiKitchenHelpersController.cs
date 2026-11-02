using App.Core.AiHelpers;
using App.Core.Exceptions;
using App.Core.Ingestion;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace App.API.Controllers;

[ApiController]
[Route("ai-helpers")]
[Authorize]
public sealed class AiKitchenHelpersController(IAiKitchenHelperService aiKitchenHelperService)
    : ControllerBase
{
    [HttpGet("status")]
    [ProducesResponseType(typeof(AiHelperStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
    {
        var status = await aiKitchenHelperService.GetStatusAsync(cancellationToken);
        return Ok(status);
    }

    [HttpPost("ingredient-substitutions")]
    [ProducesResponseType(typeof(IngredientSubstitutionResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SuggestIngredientSubstitutions(
        [FromBody] SuggestIngredientSubstitutionsRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var result = await aiKitchenHelperService.SuggestIngredientSubstitutionsAsync(
                request,
                cancellationToken
            );
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
        catch (LlmHostUnavailableException ex)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new ProblemDetails
                {
                    Status = StatusCodes.Status503ServiceUnavailable,
                    Title = "LLM Host Unavailable",
                    Detail = ex.Message,
                }
            );
        }
    }

    [HttpPost("draft-meal-plan")]
    [ProducesResponseType(typeof(DraftMealPlanResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> DraftWeeklyMealPlan(
        [FromBody] DraftWeeklyMealPlanRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var result = await aiKitchenHelperService.DraftWeeklyMealPlanAsync(
                request,
                cancellationToken
            );
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
        catch (LlmHostUnavailableException ex)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new ProblemDetails
                {
                    Status = StatusCodes.Status503ServiceUnavailable,
                    Title = "LLM Host Unavailable",
                    Detail = ex.Message,
                }
            );
        }
    }
}
