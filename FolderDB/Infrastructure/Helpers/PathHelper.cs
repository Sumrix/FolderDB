using System;
using System.IO;

namespace FolderDB.Infrastructure.Helpers;

public static class PathHelper
{
    public static readonly StringComparer OSDependedPathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly char[] _invalidChars = Path.GetInvalidFileNameChars();

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
                span[i] = _invalidChars.AsSpan().Contains(c) ? '_' : c;
            }
        });
    }

    /// <remarks>
    /// Also exceptions from:
    /// <see cref="Path.GetFullPath(string)"/>.
    /// </remarks>
    public static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}