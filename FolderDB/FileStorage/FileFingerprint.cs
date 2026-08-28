using System;

namespace FolderDB.FileStorage;

// TODO: Check if there are any benefits of being a struct vs class here.
/// <summary>
/// Represents the meaningful observed state of a file.
/// </summary>
/// <remarks>
/// <para>
/// FolderDB compares fingerprints to decide whether a file changed since it was last read, so the
/// type carries only what can be observed without opening the file. Two fingerprints that compare
/// equal describe a file that FolderDB treats as unchanged.
/// </para>
/// <para>
/// When <see cref="Exists"/> is <see langword="false"/>, both <see cref="LastWriteUtc"/> and
/// <see cref="Length"/> are <see langword="null"/>. A file that cannot be examined is reported as
/// not existing.
/// </para>
/// </remarks>
/// <param name="LastWriteUtc">
/// The time the file was last written to, in UTC, or <see langword="null"/> if the file does not
/// exist.
/// </param>
/// <param name="Length">
/// The size of the file in bytes, or <see langword="null"/> if the file does not exist.
/// </param>
/// <param name="Exists">
/// <see langword="true"/> if the file was observed to exist; otherwise, <see langword="false"/>.
/// </param>
public readonly record struct FileFingerprint(DateTime? LastWriteUtc, long? Length, bool Exists)
{
    /// <summary>
    /// Returns a string that represents the fingerprint.
    /// </summary>
    /// <returns>A string that represents the fingerprint.</returns>
    public override string ToString()
        => Exists
            ? $"exists=true length={Length} lastWriteUtc={LastWriteUtc:O}"
            : "exists=false";
}
