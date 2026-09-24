using ProjectManagementWeb.Domain.Enums;

namespace ProjectManagementWeb.Application.Common;

public interface IBusinessCodeGenerator
{
    Task<string?> GenerateAsync(BusinessCodeType codeType, DateTimeOffset now, CancellationToken cancellationToken);
}
