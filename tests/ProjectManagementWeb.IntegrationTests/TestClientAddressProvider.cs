using System.Collections.Concurrent;
using ProjectManagementWeb.Application.Common;

namespace ProjectManagementWeb.IntegrationTests;

internal sealed class TestClientAddressProvider : IClientAddressProvider
{
    private readonly ConcurrentDictionary<string, byte> _addresses = new(StringComparer.Ordinal);
    private string _clientAddress;

    public TestClientAddressProvider()
    {
        _clientAddress = $"test-{Guid.NewGuid():N}";
        _addresses.TryAdd(_clientAddress, 0);
    }

    public string ClientAddress
    {
        get => _clientAddress;
        set
        {
            _clientAddress = value;
            _addresses.TryAdd(value, 0);
        }
    }

    public IReadOnlyCollection<string> UsedAddresses => _addresses.Keys.ToArray();

    public string GetClientAddress() => ClientAddress;
}
