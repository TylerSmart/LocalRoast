using App.Core.Exceptions;
using App.Core.Recipes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace App.API.Controllers;

[ApiController]
[Route("recipes/{recipeId:guid}/cook-logs")]
[Authorize]
public sealed class RecipeCookLogsController(ICookLogService cookLogService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<CookLogDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCookLogs(Guid recipeId, CancellationToken cancellationToken)
    {
        try
        {
            var logs = await cookLogService.GetCookLogsForRecipeAsync(recipeId, cancellationToken);
            return Ok(logs);
        }
        catch (NotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost]
    [ProducesResponseType(typeof(CookLogMutationResultDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateCookLog(
        Guid recipeId,
        [FromBody] UpsertCookLogRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var result = await cookLogService.CreateCookLogAsync(
                recipeId,
                request,
                cancellationToken
            );
            return Created($"/api/recipes/{recipeId}/cook-logs/{result.CookLog.Id}", result);
        }
        catch (NotFoundException)
        {
            return NotFound();
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

    [HttpPut("{cookLogId:guid}")]
    [ProducesResponseType(typeof(CookLogMutationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateCookLog(
        Guid recipeId,
        Guid cookLogId,
        [FromBody] UpsertCookLogRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var result = await cookLogService.UpdateCookLogAsync(
                recipeId,
                cookLogId,
                request,
                cancellationToken
            );
            return result is not null ? Ok(result) : NotFound();
        }
        catch (NotFoundException)
        {
            return NotFound();
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

    [HttpDelete("{cookLogId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteCookLog(
        Guid recipeId,
        Guid cookLogId,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var deleted = await cookLogService.DeleteCookLogAsync(
                recipeId,
                cookLogId,
                cancellationToken
            );
            return deleted ? NoContent() : NotFound();
        }
        catch (NotFoundException)
        {
            return NotFound();
        }
    }
}
