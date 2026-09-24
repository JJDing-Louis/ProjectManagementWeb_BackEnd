using ProjectManagementWeb.Domain.Enums;

namespace ProjectManagementWeb.Application.Common;

public static class BusinessCodeFormatter
{
    public static string Format(BusinessCodeType codeType, DateOnly businessDate, int sequence)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sequence, 999_999);
        string prefix = codeType == BusinessCodeType.Project ? "PRJ" : "TASK";
        return $"{prefix}-{businessDate:yyyyMMdd}{sequence:000000}";
    }
}
