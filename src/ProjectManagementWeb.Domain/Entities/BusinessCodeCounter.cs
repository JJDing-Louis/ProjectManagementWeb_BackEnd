using ProjectManagementWeb.Domain.Enums;

namespace ProjectManagementWeb.Domain.Entities;

public sealed class BusinessCodeCounter
{
    private BusinessCodeCounter() { }

    public BusinessCodeCounter(BusinessCodeType codeType, DateOnly businessDate)
    {
        CodeType = codeType;
        BusinessDate = businessDate;
        LastValue = 1;
    }

    public BusinessCodeType CodeType { get; private set; }
    public DateOnly BusinessDate { get; private set; }
    public int LastValue { get; private set; }

    public bool TryIncrement()
    {
        if (LastValue >= 999_999)
        {
            return false;
        }

        LastValue++;
        return true;
    }
}
