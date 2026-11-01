using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Indexing;
using FolderDB.Indexing.Reconciliation;
using FolderDB.Infrastructure.Helpers;
using FolderDB.Infrastructure.Logging;
using FolderDB.Retry;
using Microsoft.Extensions.Logging;

namespace FolderDB.Runtime;

internal sealed class DirectoryReconciler<TKey, TRecord, TProjection>(
    string collectionPath,
    CollectionIndex<TKey, TRecord, TProjection> index,
    FileReconciler<TKey, TRecord, TProjection> fileReconciler,
    Action<string, bool> requestFileReconcile,
    ILogger logger)
    where TRecord : class, IRecord<TKey>
    where TKey : notnull
{
    public async Task<RetryDecision> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        using var _ = logger.BeginMethodScope();
        var stopwatch = Stopwatch.StartNew();
        logger.LogDebug("Rescan started: path=\"{Path}\"", collectionPath);

        IEnumerable<string> filesOnDisk;
        try
        {
            filesOnDisk = Directory.GetFiles(collectionPath, "*.json", SearchOption.TopDirectoryOnly)
                .Select(path => Path.GetFileName(path));
        }
        catch (DirectoryNotFoundException)
        {
            using (var exclusiveIndexScope = await index.EnterExclusiveScopeAsync(cancellationToken))
            {
                logger.LogWarning("Rescan directory missing, index cleared: path=\"{Path}\"", collectionPath);
                exclusiveIndexScope.Clear();
            }

            return RetryDecision.Complete;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rescan directory read failed, will retry: path=\"{Path}\"", collectionPath);
            return RetryDecision.RetryWithBackoff;
        }

        HashSet<string> processingFileNames;
        using (var sharedIndexScope = await index.EnterSharedScopeAsync(cancellationToken))
        {
            processingFileNames = filesOnDisk
                .Union(sharedIndexScope.Files.Keys)
                .ToHashSet(PathHelper.OSDependedPathComparer);
        }

        foreach (var fileName in processingFileNames)
        {
            var filePath = Path.Combine(collectionPath, fileName);
            var result = await fileReconciler.ReconcileAsync(filePath, cancellationToken);
            if (result != RetryDecision.Complete)
            {
                requestFileReconcile(filePath, result == RetryDecision.RetryWithMinBackoff);
            }
        }

        using (var sharedIndexScope = await index.EnterSharedScopeAsync(cancellationToken))
        {
            logger.LogDebug(
                "Rescan completed: path=\"{Path}\" files={Files} entries={Entries} durationMs={DurationMs}",
                collectionPath,
                processingFileNames.Count,
                sharedIndexScope.Records.Count,
                stopwatch.ElapsedMilliseconds);
        }

        return RetryDecision.Complete;
    }
}
