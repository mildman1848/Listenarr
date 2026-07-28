using System.Security.Cryptography;
using System.Text;

namespace Listenarr.Application.Downloads.Contracts;

public static class FileMoveOperationIdentity
{
    public static Guid Create(
        string scope,
        params object?[] stableParts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var payload = string.Join(
            "\0",
            new[] { scope }.Concat(
                stableParts.Select(part =>
                    Convert.ToString(
                        part,
                        System.Globalization.CultureInfo.InvariantCulture)
                    ?? string.Empty)));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return new Guid(hash.AsSpan(0, 16));
    }
}
