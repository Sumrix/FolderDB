using System;
using System.IO;
using FolderDB.Infrastructure.Helpers;
using FolderDB.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderDB.Infrastructure.Watching;

public class PathWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly ILogger<PathWatcher> _logger;
    private readonly string? _extensionFilter;

    /// <remarks>
    /// Also exceptions from:
    /// <see cref="FileSystemWatcher.EnableRaisingEvents"/>.
    /// </remarks>
    public bool EnableRaisingEvents
    {
        get => _watcher.EnableRaisingEvents;
        set => _watcher.EnableRaisingEvents = value;
    }

    public event EventHandler<string>? Changed;
    public event EventHandler<Exception>? Error;

    /// <remarks>
    /// Also exceptions from:
    /// <see cref="FileSystemWatcher(string, string)"/>.
    /// </remarks>
    public PathWatcher(string path, string? extensionFilter = null, ILogger<PathWatcher>? logger = null)
    {
        _logger = logger ?? NullLogger<PathWatcher>.Instance;
        _extensionFilter = extensionFilter;
        var filter = extensionFilter != null ? "*" + _extensionFilter : "*";
        _watcher = new FileSystemWatcher(path, filter)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024
        };

        _watcher.Created += (_, e) => OnChange(e.FullPath);
        _watcher.Changed += (_, e) => OnChange(e.FullPath);
        _watcher.Deleted += (_, e) => OnChange(e.FullPath);
        _watcher.Renamed += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.OldFullPath))
                OnChange(e.OldFullPath);
            OnChange(e.FullPath);
        };
        _watcher.Error += OnError;
    }

    private void OnChange(string path)
    {
        using var scope = _logger.BeginMethodScope();
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                _logger.LogWarning("FileSystemWatcher produced empty path: watcherPath=\"{Path}\"", _watcher.Path);
                return;
            }

            path = PathHelper.NormalizePath(path);
            if (!MatchesFilter(path))
            {
                _logger.LogTrace(
                    "Watcher path ignored by filter: path=\"{Path}\" filter={Filter}",
                    path,
                    _watcher.Filter);
                return;
            }

            Changed?.Invoke(this, path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to normalize watcher path: rawPath=\"{RawPath}\"", path);
            Error?.Invoke(this, ex);
        }
    }

    private void OnError(object _, ErrorEventArgs e)
    {
        using var scope = _logger.BeginMethodScope();
        var ex = e.GetException();
        _logger.LogError(ex, "FileSystemWatcher faulted: watcherPath=\"{Path}\" filter={Filter}", _watcher.Path, _watcher.Filter);
        Error?.Invoke(this, ex);
    }

    private bool MatchesFilter(string path)
    {
        return _extensionFilter is null || path.EndsWith(_extensionFilter, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _watcher.Dispose();
    }
}
