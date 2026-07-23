using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class AudiobookContentMoveServiceTests
{
    [Fact]
    public async Task MoveContentsAsync_TempParentReplacedAfterHandleOpen_DoesNotCreateOutsideBoundary()
    {
        var root = FileService.GetTempDirectory("content-move-temp-parent-race-root");
        var targetParent = Path.Join(root, "destination-parent");
        var displacedParent = Path.Join(root, "destination-parent.original");
        var external = FileService.GetTempDirectory("content-move-temp-parent-race-external");
        var probe = Path.Join(root, "link-probe");
        Directory.CreateDirectory(targetParent);
        if (!TryCreateTempDirectoryLink(probe, external))
        {
            return;
        }
        Directory.Delete(probe);

        var source = FileService.GetTempDirectory("content-move-temp-parent-race-source");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(targetParent, "Book");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var tempDirectory = Path.Join(
            targetParent,
            Path.GetFileName(target) + ".tmp-" + request.JobId.ToString("N"));
        var hookRan = false;
        var tempAlreadyExistedAtHook = false;
        void ReplaceParent(string path)
        {
            if (hookRan || !string.Equals(path, tempDirectory, StringComparison.Ordinal))
            {
                return;
            }

            hookRan = true;
            tempAlreadyExistedAtHook = Directory.Exists(tempDirectory);
            Directory.Move(targetParent, displacedParent);
            Directory.CreateSymbolicLink(targetParent, external);
        }

        using var hook = ExclusiveDirectoryCreator.PushBeforeCreateHook(ReplaceParent);
        try
        {
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new DisableAtomicRename());
            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(hookRan);
            Assert.False(tempAlreadyExistedAtHook);
            Assert.True(File.Exists(sourceFile));
            Assert.Empty(Directory.EnumerateFileSystemEntries(external));
            Assert.False(Directory.Exists(Path.Join(external, Path.GetFileName(target))));
            Assert.False(Directory.Exists(Path.Join(
                external,
                Path.GetFileName(tempDirectory))));
        }
        finally
        {
            TryDeleteTempDirectoryLink(targetParent);
            if (Directory.Exists(displacedParent) && !Directory.Exists(targetParent))
            {
                Directory.Move(displacedParent, targetParent);
            }
        }
    }

    [Fact]
    public async Task MoveContentsAsync_TempParentReplacedAtPublication_DoesNotPublishSubstituteTree()
    {
        var root = FileService.GetTempDirectory("content-move-temp-publication-race-root");
        var targetParent = Path.Join(root, "destination-parent");
        var displacedParent = Path.Join(root, "destination-parent.original");
        var external = FileService.GetTempDirectory("content-move-temp-publication-race-external");
        var probe = Path.Join(root, "link-probe");
        Directory.CreateDirectory(targetParent);
        if (!TryCreateTempDirectoryLink(probe, external))
        {
            return;
        }
        Directory.Delete(probe);

        var source = FileService.GetTempDirectory("content-move-temp-publication-race-source");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(targetParent, "Book");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var tempName = Path.GetFileName(target) + ".tmp-" + request.JobId.ToString("N");
        var externalTemp = Path.Join(external, tempName);
        var externalTarget = Path.Join(external, Path.GetFileName(target));
        var publicationRan = false;
        void ReplaceParentAndSubstituteTemp()
        {
            publicationRan = true;
            Directory.Move(targetParent, displacedParent);
            var originalTemp = Path.Join(displacedParent, tempName);
            Directory.CreateDirectory(externalTemp);
            foreach (var file in Directory.EnumerateFiles(originalTemp))
            {
                File.Copy(file, Path.Join(externalTemp, Path.GetFileName(file)));
            }

            if (OperatingSystem.IsWindows())
            {
                if (!TryCreateTempDirectoryJunction(targetParent, external))
                {
                    throw new IOException("The target parent replacement junction could not be created.");
                }
            }
            else
            {
                Directory.CreateSymbolicLink(targetParent, external);
            }
        }

        try
        {
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new ReplaceTempParentAtPublication(ReplaceParentAndSubstituteTemp));
            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(publicationRan);
            Assert.False(Directory.Exists(externalTarget));
            Assert.True(File.Exists(sourceFile));
        }
        finally
        {
            TryDeleteTempDirectoryLink(targetParent);
            if (Directory.Exists(displacedParent) && !Directory.Exists(targetParent))
            {
                Directory.Move(displacedParent, targetParent);
            }
        }
    }

    [Fact]
    public async Task MoveContentsAsync_TempParentReplacedBeforeMarkerCreation_DoesNotWriteOutsideBoundary()
    {
        var root = FileService.GetTempDirectory("content-move-temp-marker-parent-race-root");
        var targetParent = Path.Join(root, "destination-parent");
        var displacedParent = Path.Join(root, "destination-parent.original");
        var external = FileService.GetTempDirectory("content-move-temp-marker-parent-race-external");
        var probe = Path.Join(root, "link-probe");
        Directory.CreateDirectory(targetParent);
        if (!TryCreateTempDirectoryLink(probe, external))
        {
            return;
        }
        Directory.Delete(probe);

        var source = FileService.GetTempDirectory("content-move-temp-marker-parent-race-source");
        var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
        var target = Path.Join(targetParent, "Book");
        var request = await CreateLeasedMoveRequestAsync(source, target);
        var tempDirectory = Path.Join(
            targetParent,
            Path.GetFileName(target) + ".tmp-" + request.JobId.ToString("N"));
        var replacementRan = false;
        var tempExistedBeforeReplacement = false;
        void ReplaceParent()
        {
            replacementRan = true;
            tempExistedBeforeReplacement = Directory.Exists(tempDirectory);
            Directory.Move(targetParent, displacedParent);
            Directory.CreateSymbolicLink(targetParent, external);
        }

        try
        {
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new ReplaceTempParentBeforeMarkerCreation(ReplaceParent));
            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(replacementRan);
            Assert.True(tempExistedBeforeReplacement);
            Assert.True(File.Exists(sourceFile));
            Assert.Empty(Directory.EnumerateFileSystemEntries(external));
            Assert.False(File.Exists(Path.Join(
                external,
                Path.GetFileName(tempDirectory),
                ".listenarr-temp-owner.json")));
        }
        finally
        {
            TryDeleteTempDirectoryLink(targetParent);
            if (Directory.Exists(displacedParent) && !Directory.Exists(targetParent))
            {
                Directory.Move(displacedParent, targetParent);
            }
        }
    }

    private sealed class DisableAtomicRename : IMoveFaultInjector
    {
        public bool AllowAtomicRename => false;
    }

    private sealed class ReplaceTempParentAtPublication(Action replaceParent)
        : IMoveFaultInjector
    {
        private bool _replaced;

        public bool AllowAtomicRename => false;

        public void OnTempPublication(
            Guid jobId,
            TempPublicationFaultPoint faultPoint)
        {
            if (_replaced || faultPoint != TempPublicationFaultPoint.BeforePublication)
            {
                return;
            }

            _replaced = true;
            replaceParent();
        }
    }

    private sealed class ReplaceTempParentBeforeMarkerCreation(Action replaceParent)
        : IMoveFaultInjector
    {
        private bool _replaced;

        public bool AllowAtomicRename => false;

        public void OnOwnershipMarkerWrite(
            Guid jobId,
            OwnershipMarkerKind markerKind,
            OwnershipMarkerWriteFaultPoint faultPoint)
        {
            if (_replaced
                || markerKind != OwnershipMarkerKind.TemporaryDirectory
                || faultPoint != OwnershipMarkerWriteFaultPoint.BeforeTemporaryFileCreation)
            {
                return;
            }

            _replaced = true;
            replaceParent();
        }
    }

    private static bool TryCreateTempDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return OperatingSystem.IsWindows()
                && TryCreateTempDirectoryJunction(linkPath, targetPath);
        }
    }

    private static bool TryCreateTempDirectoryJunction(string linkPath, string targetPath)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/d /c mklink /J \"{linkPath}\" \"{targetPath}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
            process?.WaitForExit();
            return process?.ExitCode == 0 && Directory.Exists(linkPath);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteTempDirectoryLink(string path)
    {
        try
        {
            if (Directory.Exists(path)
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(path);
            }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException)
        {
            // Best effort test cleanup. BaseTests removes the temporary roots.
        }
    }
}
