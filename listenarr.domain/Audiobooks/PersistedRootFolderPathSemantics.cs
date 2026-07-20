using Listenarr.Domain.Common;

namespace Listenarr.Domain.Audiobooks;

public readonly record struct PersistedRootFolderPathSemantics(
    FileSystemPathSemantics Semantics,
    bool DetectAmbiguousCaseMatches);

public static class RootFolderPathSemantics
{
    public static PersistedRootFolderPathSemantics? ResolvePersisted(RootFolder root)
    {
        ArgumentNullException.ThrowIfNull(root);

        FileSystemPathSyntax syntax;
        if (root.Path.StartsWith("/", StringComparison.Ordinal))
        {
            syntax = FileSystemPathSyntax.Unix;
        }
        else if (
            (root.Path.Length >= 3
                && char.IsAsciiLetter(root.Path[0])
                && root.Path[1] == ':'
                && root.Path[2] is '\\' or '/')
            || root.Path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            syntax = FileSystemPathSyntax.Windows;
        }
        else
        {
            return null;
        }

        var sensitivity = root.CaseSensitivityMode switch
        {
            FileSystemCaseSensitivityMode.Sensitive => FileSystemCaseSensitivity.Sensitive,
            FileSystemCaseSensitivityMode.Insensitive => FileSystemCaseSensitivity.Insensitive,
            FileSystemCaseSensitivityMode.Auto
                when root.PathIdentityState == PathIdentityState.Valid
                    && root.ResolvedCaseSensitivity != FileSystemCaseSensitivity.Unknown =>
                root.ResolvedCaseSensitivity,
            _ => FileSystemCaseSensitivity.Sensitive
        };
        var detectAmbiguousCaseMatches =
            root.CaseSensitivityMode == FileSystemCaseSensitivityMode.Auto
            && (root.PathIdentityState != PathIdentityState.Valid
                || root.ResolvedCaseSensitivity == FileSystemCaseSensitivity.Unknown);

        return new PersistedRootFolderPathSemantics(
            new FileSystemPathSemantics(syntax, sensitivity),
            detectAmbiguousCaseMatches);
    }
}
