using App.Core.Exceptions;
using App.Core.MealPlans;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace App.API.Controllers;

[ApiController]
[Route("meal-plan-templates")]
[Authorize]
public sealed class MealPlanTemplatesController(IMealPlanTemplateService templateService)
    : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(
        typeof(IReadOnlyList<MealPlanTemplateSummaryDto>),
        StatusCodes.Status200OK
    )]
    public async Task<IActionResult> GetTemplates(CancellationToken cancellationToken)
    {
        var templates = await templateService.GetTemplatesAsync(cancellationToken);
        return Ok(templates);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(MealPlanTemplateDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTemplateById(Guid id, CancellationToken cancellationToken)
    {
        var template = await templateService.GetTemplateByIdAsync(id, cancellationToken);
        return template is not null ? Ok(template) : NotFound();
    }

    [HttpPost]
    [ProducesResponseType(typeof(MealPlanTemplateDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateTemplate(
        [FromBody] UpsertMealPlanTemplateRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var template = await templateService.CreateTemplateAsync(request, cancellationToken);
            return CreatedAtAction(nameof(GetTemplateById), new { id = template.Id }, template);
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

    [HttpPost("from-week")]
    [ProducesResponseType(typeof(MealPlanTemplateDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateTemplateFromWeek(
        [FromBody] CreateTemplateFromWeekRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var template = await templateService.CreateTemplateFromWeekAsync(
                request,
                cancellationToken
            );
            return CreatedAtAction(nameof(GetTemplateById), new { id = template.Id }, template);
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
    [ProducesResponseType(typeof(MealPlanTemplateDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateTemplate(
        Guid id,
        [FromBody] UpsertMealPlanTemplateRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var template = await templateService.UpdateTemplateAsync(
                id,
                request,
                cancellationToken
            );
            return template is not null ? Ok(template) : NotFound();
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
    public async Task<IActionResult> DeleteTemplate(Guid id, CancellationToken cancellationToken)
    {
        var deleted = await templateService.DeleteTemplateAsync(id, cancellationToken);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPost("{id:guid}/apply")]
    [ProducesResponseType(typeof(MealPlanWeekDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ApplyTemplateToWeek(
        Guid id,
        [FromBody] ApplyMealPlanTemplateRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var week = await templateService.ApplyTemplateToWeekAsync(
                id,
                request,
                cancellationToken
            );
            return week is not null ? Ok(week) : NotFound();
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
