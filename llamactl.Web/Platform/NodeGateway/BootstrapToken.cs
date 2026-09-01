using System.Security.Cryptography;
using System.Text;

namespace llamactl.Web.Platform.NodeGateway;

internal static class BootstrapToken
{
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool Matches(string token, string expectedHash)
    {
        var actualHash = Encoding.ASCII.GetBytes(Hash(token));
        var storedHash = Encoding.ASCII.GetBytes(expectedHash);

        return actualHash.Length == storedHash.Length
            && CryptographicOperations.FixedTimeEquals(actualHash, storedHash);
    }
}