using System.Security.Claims;
using App.Core.Exceptions;
using App.Core.Maintenance;
using Microsoft.AspNetCore.Mvc;

namespace App.API.Http;

internal static class AppDataBackupHttpHelper
{
    internal const long MaxBackupUploadBytes = 1_073_741_824L;

    public static Guid? TryGetUserId(ClaimsPrincipal user)
    {
        var raw = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    public static async Task<IActionResult> ExportBackupAsync(
        ControllerBase controller,
        IAppDataMaintenanceService maintenanceService,
        bool includeMedia,
        CancellationToken cancellationToken
    )
    {
        var tempStream = CreateTempBackupFileStream();
        try
        {
            await maintenanceService.ExportBackupToStreamAsync(
                tempStream,
                new ExportAppDataBackupRequest(includeMedia),
                cancellationToken
            );
            await tempStream.FlushAsync(cancellationToken);
            tempStream.Position = 0;

            var fileName = $"localroast-backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip";
            return controller.File(tempStream, "application/zip", fileName);
        }
        catch
        {
            await tempStream.DisposeAsync();
            throw;
        }
    }

    public static async Task<IActionResult> ImportBackupAsync(
        ControllerBase controller,
        IAppDataMaintenanceService maintenanceService,
        IFormFile? file,
        Guid? importingUserId,
        CancellationToken cancellationToken
    )
    {
        if (file is null || file.Length == 0)
        {
            return controller.BadRequest(
                new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Validation Error",
                    Detail = "A valid .zip backup archive file is required.",
                }
            );
        }

        await using var tempStream = CreateTempBackupFileStream();
        await using (var uploadStream = file.OpenReadStream())
        {
            await uploadStream.CopyToAsync(tempStream, cancellationToken);
        }

        tempStream.Position = 0;

        try
        {
            var result = await maintenanceService.ImportBackupAsync(
                tempStream,
                importingUserId,
                cancellationToken
            );
            return controller.Ok(result);
        }
        catch (DomainValidationException ex)
        {
            return controller.BadRequest(
                new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = ex.Title,
                    Detail = ex.Message,
                }
            );
        }
    }

    private static FileStream CreateTempBackupFileStream()
    {
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"localroast-backup-{Guid.NewGuid():N}.zip"
        );
        return new FileStream(
            tempPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 65536,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous
        );
    }
}
