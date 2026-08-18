using System;
using FolderDB.Infrastructure.Helpers;

namespace FolderDB.Tests;

// The trailing dot and space rule only applies on Windows, and the invalid character set itself is
// OS dependent, so the cases below assert what the host OS is supposed to do rather than skipping.
// '/' is the one character that is invalid on every platform.
public class PathHelperTests
{
    [Theory]
    [InlineData("users", "users")]
    [InlineData("a.b", "a.b")]
    [InlineData("..users", "..users")]
    [InlineData(".indices", ".indices")]
    [InlineData("_", "_")]
    [InlineData("a/b", "a_b")]
    [InlineData("/", "_")]
    [InlineData("a/b/c", "a_b_c")]
    [InlineData("", "")]
    public void SanitizeFileName_AllPlatforms(string name, string expected)
    {
        Assert.Equal(expected, PathHelper.SanitizeFileName(name));
    }

    [Theory]
    [InlineData("users.", "users_")]
    [InlineData("users ", "users_")]
    [InlineData("users..", "users__")]
    [InlineData("users. .", "users___")]
    [InlineData("a/b.", "a_b_")]
    [InlineData(".", "_")]
    [InlineData("..", "__")]
    [InlineData("   ", "___")]
    public void SanitizeFileName_OsDepended(
        string name,
        string windowsExpected)
    {
        var expected = OperatingSystem.IsWindows() ? windowsExpected : name;

        Assert.Equal(expected, PathHelper.SanitizeFileName(name));
    }
}
