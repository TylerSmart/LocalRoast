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
}
