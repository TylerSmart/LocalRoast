using App.Core.Exceptions;
using App.Core.MealPlans;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace App.API.Controllers;

[ApiController]
[Route("meal-plans")]
[Authorize]
public sealed class MealPlansController(IMealPlanService mealPlanService) : ControllerBase
{
    [HttpGet("week")]
    [ProducesResponseType(typeof(MealPlanWeekDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetWeek(
        [FromQuery] DateOnly? weekStart,
        CancellationToken cancellationToken
    )
    {
        var targetDate = weekStart ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var week = await mealPlanService.GetWeekAsync(targetDate, cancellationToken);
        return Ok(week);
    }

    [HttpGet]
    [ProducesResponseType(typeof(MealPlanRangeResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetRange(
        [FromQuery] DateOnly startDate,
        [FromQuery] DateOnly endDate,
        [FromQuery] bool includeOnDeck = true,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var range = await mealPlanService.GetRangeAsync(
                startDate,
                endDate,
                includeOnDeck,
                cancellationToken
            );
            return Ok(range);
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

    [HttpGet("entries/{id:guid}")]
    [ProducesResponseType(typeof(MealPlanEntryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEntryById(Guid id, CancellationToken cancellationToken)
    {
        var entry = await mealPlanService.GetEntryByIdAsync(id, cancellationToken);
        return entry is not null ? Ok(entry) : NotFound();
    }

    [HttpPost("entries")]
    [ProducesResponseType(typeof(MealPlanEntryDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateEntry(
        [FromBody] CreateMealPlanEntryRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var entry = await mealPlanService.CreateEntryAsync(request, cancellationToken);
            return CreatedAtAction(nameof(GetEntryById), new { id = entry.Id }, entry);
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

    [HttpPut("entries/{id:guid}")]
    [ProducesResponseType(typeof(MealPlanEntryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateEntry(
        Guid id,
        [FromBody] UpdateMealPlanEntryRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var entry = await mealPlanService.UpdateEntryAsync(id, request, cancellationToken);
            return entry is not null ? Ok(entry) : NotFound();
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

    [HttpPost("entries/{id:guid}/move")]
    [ProducesResponseType(typeof(MealPlanEntryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MoveEntry(
        Guid id,
        [FromBody] MoveMealPlanEntryRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var entry = await mealPlanService.MoveEntryAsync(id, request, cancellationToken);
            return entry is not null ? Ok(entry) : NotFound();
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

    [HttpPost("entries/{id:guid}/cook-tonight")]
    [ProducesResponseType(typeof(MealPlanEntryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CookTonight(
        Guid id,
        [FromBody] CookTonightRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var entry = await mealPlanService.CookTonightAsync(id, request, cancellationToken);
            return entry is not null ? Ok(entry) : NotFound();
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

    [HttpDelete("entries/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteEntry(Guid id, CancellationToken cancellationToken)
    {
        var deleted = await mealPlanService.DeleteEntryAsync(id, cancellationToken);
        return deleted ? NoContent() : NotFound();
    }

    [HttpDelete("week")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ClearWeek(
        [FromQuery] DateOnly weekStart,
        [FromQuery] bool includeOnDeck = false,
        CancellationToken cancellationToken = default
    )
    {
        await mealPlanService.ClearWeekAsync(weekStart, includeOnDeck, cancellationToken);
        return NoContent();
    }
}
