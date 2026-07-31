using System.Security.Cryptography;
using System.Text;

namespace Listenarr.Infrastructure.FileSystem;

internal static class ManagedDirectoryIdentity
{
    internal const int CurrentVersion = 2;
    private const string Prefix = "listenarr-directory-v2";

    internal static bool Matches(
        int? version,
        string? value,
        string token,
        string nativeIdentity) =>
        version == CurrentVersion
        && !string.IsNullOrWhiteSpace(value)
        && string.Equals(
            value,
            Create(token, nativeIdentity),
            StringComparison.Ordinal);

    internal static string Create(string token, string nativeIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeIdentity);
        var nativeHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(nativeIdentity)))
            .ToLowerInvariant();
        return FormattableString.Invariant($"{Prefix}:{token}:{nativeHash}");
    }
}
