using System.Security.Cryptography;
using System.Text;

namespace ProjectManagementWeb.Infrastructure.Persistence;

internal static class SeedIds
{
    public static Guid Create(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"ProjectManagementWeb:{value}"));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        return new Guid(bytes);
    }
}
