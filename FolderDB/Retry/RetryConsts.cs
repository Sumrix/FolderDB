using System;

namespace FolderDB.Retry;

public static class RetryConsts
{
    // The limit is arbitrary, but we follow Polly's convention of using 1 day.
    public static readonly TimeSpan MaxDelay = TimeSpan.FromDays(1);
}
