namespace App.Core.Maintenance;

public interface IAppDataMaintenanceService
{
    Task<ResetAppDataResultDto> ResetAppDataAsync(
        ResetAppDataRequest? request = null,
        CancellationToken cancellationToken = default
    );

    Task ExportBackupToStreamAsync(
        Stream destination,
        ExportAppDataBackupRequest? request = null,
        CancellationToken cancellationToken = default
    );

    Task<ImportAppDataBackupResultDto> ImportBackupAsync(
        Stream archiveStream,
        Guid? importingUserId = null,
        CancellationToken cancellationToken = default
    );
}
