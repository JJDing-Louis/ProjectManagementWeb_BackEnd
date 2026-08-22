using ProjectManagementWeb.Application.Common;

namespace ProjectManagementWeb.Application.Preferences;

public interface IPreferenceService
{
    Task<ServiceResult<PreferenceResponse>> GetAsync(CancellationToken cancellationToken);
    Task<ServiceResult<PreferenceResponse>> UpdateAsync(UpdatePreferenceRequest request, CancellationToken cancellationToken);
}
