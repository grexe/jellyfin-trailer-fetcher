using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrailerFetcher.Services;

/// <summary>
/// Makes a newly created file/folder as usable by the rest of a library-managing
/// setup as a reference file already is - matching its permission bits and group.
/// Confirmed live: a movie's own folder, created earlier by a different user (e.g.
/// media added over SMB as one user, with Jellyfin's own process running as
/// another), denied this plugin's own process write access to it entirely - the
/// two users shared a group, but the folder's permission bits didn't grant that
/// group write access. Rather than picking one fixed mode to force everywhere,
/// matching whatever the library's own existing files already use adapts to
/// however a given deployment is actually set up.
///
/// Never changes the file's OWNER (uid) - that generally requires root, which this
/// plugin shouldn't need or want. Linux only (no-op elsewhere): group changes here
/// shell out to `chgrp`, a POSIX utility not guaranteed present/equivalent on
/// Windows, and this whole scenario - multi-user Unix permission drift under a
/// Jellyfin deployment - is a server-side concern that doesn't apply to local
/// development on macOS/Windows anyway. Best-effort throughout: any failure here
/// is logged and otherwise ignored, never blocking or failing the trailer/theme
/// song fetch that created the file/folder in the first place.
/// </summary>
public static class UnixPermissions
{
    /// <summary>Matches <paramref name="newPath"/>'s permission bits and group to <paramref name="referencePath"/>'s.</summary>
    public static void MatchTo(string newPath, string referencePath, ILogger logger)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            var mode = File.GetUnixFileMode(referencePath);
            if (File.Exists(newPath) && !Directory.Exists(newPath))
            {
                // Don't carry over an execute bit onto a regular media file just
                // because the reference happened to be a directory (which needs
                // execute to be traversable) - only meaningful for the reference's
                // own file/directory type, not something to propagate onto a file.
                mode &= ~(UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }

            File.SetUnixFileMode(newPath, mode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            logger.LogWarning("  > Could not match permission bits on {Path} to {Reference}: {Error}", newPath, referencePath, ex.Message);
        }

        try
        {
            var psi = new ProcessStartInfo("chgrp")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add($"--reference={referencePath}");
            psi.ArgumentList.Add(newPath);

            using var process = Process.Start(psi);
            if (process is null)
            {
                return;
            }

            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                logger.LogWarning("  > Could not match group ownership on {Path} to {Reference}: {Error}", newPath, referencePath, stderr.Trim());
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            // e.g. chgrp isn't installed - best-effort, the file/folder still works
            // for whichever process/user actually created it, just not necessarily
            // for others sharing the reference file's group.
            logger.LogWarning("  > Could not run chgrp for {Path}: {Error}", newPath, ex.Message);
        }
    }
}
