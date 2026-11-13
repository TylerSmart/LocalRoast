using App.Core.Exceptions;
using App.Core.Ingestion;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace App.API.Controllers;

/// <summary>Saved LLM machines/providers and the model configurations that run on them.</summary>
[ApiController]
[Route("llm-settings")]
[Authorize]
public sealed class LlmConnectionsController(ILlmConnectionService connectionService)
    : ControllerBase
{
    [HttpGet("connections")]
    [ProducesResponseType(typeof(IReadOnlyList<LlmConnectionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetConnections(CancellationToken cancellationToken) =>
        Ok(await connectionService.GetConnectionsAsync(cancellationToken));

    [HttpPost("connections")]
    [ProducesResponseType(typeof(LlmConnectionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public Task<IActionResult> CreateConnection(
        [FromBody] UpsertLlmConnectionRequest request,
        CancellationToken cancellationToken
    ) =>
        HandleAsync(async () =>
        {
            var created = await connectionService.CreateConnectionAsync(request, cancellationToken);
            return Created($"/api/llm-settings/connections/{created.Id}", created);
        });

    [HttpPut("connections/{id:guid}")]
    [ProducesResponseType(typeof(LlmConnectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public Task<IActionResult> UpdateConnection(
        Guid id,
        [FromBody] UpsertLlmConnectionRequest request,
        CancellationToken cancellationToken
    ) =>
        HandleAsync(async () =>
            Ok(await connectionService.UpdateConnectionAsync(id, request, cancellationToken))
        );

    [HttpDelete("connections/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public Task<IActionResult> DeleteConnection(Guid id, CancellationToken cancellationToken) =>
        HandleAsync(async () =>
        {
            await connectionService.DeleteConnectionAsync(id, cancellationToken);
            return NoContent();
        });

    [HttpPost("connections/{id:guid}/test")]
    [ProducesResponseType(typeof(LlmConnectionTestResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public Task<IActionResult> TestConnection(Guid id, CancellationToken cancellationToken) =>
        HandleAsync(async () =>
            Ok(await connectionService.TestConnectionAsync(id, cancellationToken))
        );

    [HttpGet("model-configurations")]
    [ProducesResponseType(typeof(IReadOnlyList<LlmModelConfigurationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetModelConfigurations(CancellationToken cancellationToken) =>
        Ok(await connectionService.GetModelConfigurationsAsync(cancellationToken));

    [HttpPost("model-configurations")]
    [ProducesResponseType(typeof(LlmModelConfigurationDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public Task<IActionResult> CreateModelConfiguration(
        [FromBody] UpsertLlmModelConfigurationRequest request,
        CancellationToken cancellationToken
    ) =>
        HandleAsync(async () =>
        {
            var created = await connectionService.CreateModelConfigurationAsync(
                request,
                cancellationToken
            );
            return Created($"/api/llm-settings/model-configurations/{created.Id}", created);
        });

    [HttpPut("model-configurations/{id:guid}")]
    [ProducesResponseType(typeof(LlmModelConfigurationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public Task<IActionResult> UpdateModelConfiguration(
        Guid id,
        [FromBody] UpsertLlmModelConfigurationRequest request,
        CancellationToken cancellationToken
    ) =>
        HandleAsync(async () =>
            Ok(
                await connectionService.UpdateModelConfigurationAsync(
                    id,
                    request,
                    cancellationToken
                )
            )
        );

    [HttpDelete("model-configurations/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public Task<IActionResult> DeleteModelConfiguration(
        Guid id,
        CancellationToken cancellationToken
    ) =>
        HandleAsync(async () =>
        {
            await connectionService.DeleteModelConfigurationAsync(id, cancellationToken);
            return NoContent();
        });

    private async Task<IActionResult> HandleAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (DomainValidationException ex)
        {
            return ProblemResult(StatusCodes.Status400BadRequest, ex.Title, ex.Message);
        }
        catch (NotFoundException ex)
        {
            return ProblemResult(StatusCodes.Status404NotFound, "Not Found", ex.Message);
        }
        catch (ConflictException ex)
        {
            return ProblemResult(StatusCodes.Status409Conflict, ex.Title, ex.Message);
        }
    }

    private ObjectResult ProblemResult(int status, string title, string detail) =>
        StatusCode(
            status,
            new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = detail,
            }
        );
}
