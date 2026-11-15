namespace App.Core.Maintenance;

public interface IAppDataMaintenanceService
{
    Task<ResetAppDataResultDto> ResetAppDataAsync(
        ResetAppDataRequest? request = null,
        CancellationToken cancellationToken = default
    );
}
