using Microsoft.EntityFrameworkCore;
using ProjectManagementWeb.Application.Common;
using ProjectManagementWeb.Domain.Entities;
using ProjectManagementWeb.Domain.Enums;
using ProjectManagementWeb.Infrastructure.Persistence;

namespace ProjectManagementWeb.Infrastructure.Services;

internal sealed class BusinessCodeGenerator : IBusinessCodeGenerator
{
    private readonly ApplicationDbContext _db;

    public BusinessCodeGenerator(ApplicationDbContext db) => _db = db;

    public async Task<string?> GenerateAsync(BusinessCodeType codeType, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        DateOnly businessDate = DateOnly.FromDateTime(now.UtcDateTime);
        string codeTypeName = codeType.ToString();
        BusinessCodeCounter? counter = await _db.BusinessCodeCounters
            .FromSqlInterpolated($"SELECT * FROM [BusinessCodeCounters] WITH (UPDLOCK, HOLDLOCK) WHERE [CodeType] = {codeTypeName} AND [BusinessDate] = {businessDate}")
            .SingleOrDefaultAsync(cancellationToken);

        if (counter is null)
        {
            counter = new BusinessCodeCounter(codeType, businessDate);
            _db.BusinessCodeCounters.Add(counter);
        }
        else if (!counter.TryIncrement())
        {
            return null;
        }

        return BusinessCodeFormatter.Format(codeType, businessDate, counter.LastValue);
    }
}
