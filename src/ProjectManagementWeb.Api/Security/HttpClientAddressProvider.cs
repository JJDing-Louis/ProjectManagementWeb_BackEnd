using System.Net;
using ProjectManagementWeb.Application.Common;

namespace ProjectManagementWeb.Api.Security;

internal sealed class HttpClientAddressProvider : IClientAddressProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpClientAddressProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string GetClientAddress()
    {
        IPAddress? address = _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress;
        return address?.MapToIPv6().ToString() ?? "unknown";
    }
}
