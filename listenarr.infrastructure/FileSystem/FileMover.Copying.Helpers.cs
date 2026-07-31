using System.Diagnostics;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private static void CapturePublishedRegistrationLease(
        IAudiobookFileRegistrationLease registrationLease,
        Action<IAudiobookFileRegistrationLease> capturePublication)
    {
        IAudiobookFileRegistrationLease? ownedLease = registrationLease;
        try
        {
            capturePublication(ownedLease);
            ownedLease = null;
        }
        finally
        {
            ownedLease?.Dispose();
        }
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Length <= max) return value;
        return value[..max] + "...";
    }

    private static ProcessStartInfo CreateRobocopyStartInfo(
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "robocopy",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments.Where(
            argument => !string.IsNullOrWhiteSpace(argument)))
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
