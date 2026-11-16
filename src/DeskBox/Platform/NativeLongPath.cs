using System.IO;

namespace DeskBox.Platform;

/// <summary>
/// Win32 path prefixing for the raw kernel32 entry points that take a path
/// string.
/// </summary>
/// <remarks>
/// Managed file APIs resolve paths beyond MAX_PATH on their own, but a
/// P/Invoke such as <c>CreateFileW</c> does not: a 264-character path comes
/// back as INVALID_HANDLE_VALUE (ERROR_PATH_NOT_FOUND) even when the file
/// exists and no other process holds it. Storage migration re-inspects every
/// source file through <c>FileService.TryCaptureSourceIdentity</c>, so one
/// deep entry (an extracted bid package with a duplicated folder level is the
/// usual cause) aborted the whole copy with "the source is busy or
/// unavailable".
///
/// The <c>\\?\</c> prefix opts that single call out of the legacy 260
/// character limit. It needs neither the machine-wide LongPathsEnabled
/// setting nor a longPathAware application manifest, so the fix travels with
/// the binary.
/// </remarks>
internal static class NativeLongPath
{
    /// <summary>
    /// Shortest path worth prefixing. 260 is the hard limit and a directory
    /// still has to leave room for a file name, so everything from 240 up is
    /// treated as potentially too long. Shorter paths are returned unchanged
    /// so ordinary call sites keep byte-for-byte identical behaviour.
    /// </summary>
    private const int ExtendedThreshold = 240;

    internal static string Extended(string path)
    {
        if (string.IsNullOrEmpty(path) ||
            path.Length < ExtendedThreshold ||
            path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }

        // "\\?\C:\a\..\b" is passed to the kernel verbatim (no dot-segment or
        // separator normalisation), and a relative path cannot be extended at
        // all, so only fully qualified paths are rewritten.
        if (!Path.IsPathFullyQualified(path))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
    }
}
