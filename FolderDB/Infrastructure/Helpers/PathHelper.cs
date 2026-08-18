using System;
using System.IO;
using System.Security;

namespace FolderDB.Infrastructure.Helpers;

public static class PathHelper
{
    public static readonly StringComparer OSDependedPathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    public static string SanitizeFileName(string name)
    {
        return string.Create(name.Length, name, (span, original) =>
        {
            int i = original.Length - 1;

            if (OperatingSystem.IsWindows())
            {
                for (; i >= 0; i--)
                {
                    if (original[i] is not (' ' or '.')) break;

                    span[i] = '_';
                }
            }

            for (; i >= 0; i--)
            {
                char c = original[i];
                span[i] = InvalidChars.Contains(c) ? '_' : c;
            }
        });
    }

    /// <exception cref="ArgumentNullException">The path is null.</exception>
    /// <exception cref="ArgumentException">The system could not retrieve the absolute path.</exception>
    /// <exception cref="SecurityException">The caller does not have the required permissions.</exception>
    /// <exception cref="NotSupportedException">The path contains a format that is not supported.</exception>
    /// <exception cref="PathTooLongException">The specified path, file name, or both exceed the system-defined maximum length.</exception>
    public static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}