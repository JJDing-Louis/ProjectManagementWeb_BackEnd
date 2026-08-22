namespace ProjectManagementWeb.Application.Preferences;

public sealed record PreferenceResponse(bool SkipBatchConfirmation);
public sealed record UpdatePreferenceRequest(bool SkipBatchConfirmation);
