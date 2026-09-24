using Microsoft.EntityFrameworkCore;
using ProjectManagementWeb.Application.Common;
using ProjectManagementWeb.Application.Preferences;
using ProjectManagementWeb.Domain.Constants;
using ProjectManagementWeb.Domain.Entities;
using ProjectManagementWeb.Infrastructure.Persistence;

namespace ProjectManagementWeb.Infrastructure.Services;

internal sealed class PreferenceService : IPreferenceService
{
    private readonly ApplicationDbContext _db;
    private readonly ICurrentUser _currentUser;

    public PreferenceService(ApplicationDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<ServiceResult<PreferenceResponse>> GetAsync(CancellationToken cancellationToken)
    {
        if (!_currentUser.HasFunction(SystemFunctions.PreferencesReadOwn) || _currentUser.AccountId is not Guid id)
        {
            return ServiceResult<PreferenceResponse>.Failure("forbidden", "沒有讀取偏好的權限。", 403);
        }
        UserPreference? preference = await _db.UserPreferences.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == id, cancellationToken);
        return ServiceResult<PreferenceResponse>.Success(new PreferenceResponse(preference?.SkipBatchConfirmation ?? false));
    }

    public async Task<ServiceResult<PreferenceResponse>> UpdateAsync(UpdatePreferenceRequest request, CancellationToken cancellationToken)
    {
        if (!_currentUser.HasFunction(SystemFunctions.PreferencesUpdateOwn) || _currentUser.AccountId is not Guid id)
        {
            return ServiceResult<PreferenceResponse>.Failure("forbidden", "沒有修改偏好的權限。", 403);
        }
        UserPreference? preference = await _db.UserPreferences.SingleOrDefaultAsync(x => x.AccountId == id, cancellationToken);
        if (preference is null)
        {
            preference = new UserPreference(id);
            _db.UserPreferences.Add(preference);
        }
        preference.Update(request.SkipBatchConfirmation);
        await _db.SaveChangesAsync(cancellationToken);
        return ServiceResult<PreferenceResponse>.Success(new PreferenceResponse(preference.SkipBatchConfirmation));
    }
}
