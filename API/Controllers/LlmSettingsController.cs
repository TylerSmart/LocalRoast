using App.Core.Exceptions;
using App.Core.Ingestion;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace App.API.Controllers;

[ApiController]
[Route("llm-settings")]
[Authorize]
public sealed class LlmSettingsController(ILlmSettingsService settingsService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(LlmSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSettings(CancellationToken cancellationToken)
    {
        var settings = await settingsService.GetSettingsAsync(cancellationToken);
        return Ok(settings);
    }

    [HttpGet("model-profiles")]
    [ProducesResponseType(typeof(IReadOnlyList<LlmModelProfileDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetModelProfiles(CancellationToken cancellationToken)
    {
        var profiles = await settingsService.GetModelProfilesAsync(cancellationToken);
        return Ok(profiles);
    }

    [HttpPost("model-profiles")]
    [ProducesResponseType(typeof(LlmModelProfileDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateModelProfile(
        [FromBody] UpsertLlmModelProfileRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var created = await settingsService.CreateModelProfileAsync(request, cancellationToken);
            return Created($"/api/llm-settings/model-profiles/{created.Id}", created);
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

    [HttpPut("model-profiles/{id}")]
    [ProducesResponseType(typeof(LlmModelProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateModelProfile(
        string id,
        [FromBody] UpsertLlmModelProfileRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var updated = await settingsService.UpdateModelProfileAsync(
                id,
                request,
                cancellationToken
            );
            return Ok(updated);
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

    [HttpDelete("model-profiles/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteModelProfile(
        string id,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await settingsService.DeleteModelProfileAsync(id, cancellationToken);
            return NoContent();
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

    [HttpPost("model-profiles/restore-defaults")]
    [ProducesResponseType(typeof(IReadOnlyList<LlmModelProfileDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> RestoreDefaultModelProfiles(
        CancellationToken cancellationToken
    )
    {
        var restored = await settingsService.RestoreDefaultModelProfilesAsync(cancellationToken);
        return Ok(restored);
    }

    [HttpPut]
    [ProducesResponseType(typeof(LlmSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateSettings(
        [FromBody] UpdateLlmSettingsRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var updated = await settingsService.UpdateSettingsAsync(request, cancellationToken);
            return Ok(updated);
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

    [HttpPost("test")]
    [ProducesResponseType(typeof(LlmConnectionTestResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> TestConnection(
        [FromBody] TestLlmConnectionRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var result = await settingsService.TestConnectionAsync(request, cancellationToken);
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
    }

    [HttpPost("set-ingestion-enabled")]
    [ProducesResponseType(typeof(LlmSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> SetIngestionEnabled(
        [FromQuery] bool enabled,
        CancellationToken cancellationToken
    )
    {
        var updated = await settingsService.SetIngestionEnabledAsync(enabled, cancellationToken);
        return Ok(updated);
    }
}
