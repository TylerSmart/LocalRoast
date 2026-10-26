using App.Core.Exceptions;
using App.Core.Media;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace App.API.Controllers;

[ApiController]
[Route("media")]
[Authorize]
public sealed class MediaController(IMediaStorageService mediaStorageService) : ControllerBase
{
    [HttpPost("recipe-cover")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(16_777_216)]
    [ProducesResponseType(typeof(UploadedRecipeCoverDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadRecipeCover(
        IFormFile? file,
        CancellationToken cancellationToken
    )
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(
                new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Validation Error",
                    Detail = "A valid image file is required.",
                }
            );
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await mediaStorageService.SaveRecipeCoverAsync(
                stream,
                file.FileName,
                file.Length,
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
    }

    [HttpPost("recipe-step")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(16_777_216)]
    [ProducesResponseType(typeof(UploadedMediaDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadRecipeStep(
        IFormFile? file,
        CancellationToken cancellationToken
    )
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(
                new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Validation Error",
                    Detail = "A valid image file is required.",
                }
            );
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var result = await mediaStorageService.SaveStepImageAsync(
                stream,
                file.FileName,
                file.Length,
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
    }
}
