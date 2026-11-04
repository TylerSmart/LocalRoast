namespace App.Core.Maintenance;

public interface IAppDataMaintenanceService
{
    Task<ResetAppDataResultDto> ResetAppDataAsync(CancellationToken cancellationToken = default);
}
