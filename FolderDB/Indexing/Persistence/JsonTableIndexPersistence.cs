using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using FolderDB.Indexing.State;
using FolderDB.Infrastructure.Logging;
using Microsoft.Extensions.Logging;

namespace FolderDB.Indexing.Persistence;

public class JsonTableIndexPersistence<TKey, TProjection> : ITableIndexPersistence<TKey, TProjection>
    where TKey : notnull
{
    private readonly JsonTypeInfo<TKey> _keyTypeInfo;
    private readonly JsonTypeInfo<TProjection> _projectionTypeInfo;
    private readonly IEqualityComparer<TKey> _keyEqualityComparer;
    private readonly ILogger<JsonTableIndexPersistence<TKey, TProjection>> _logger;

    public JsonTableIndexPersistence(
        JsonTypeInfo<TKey> keyTypeInfo,
        JsonTypeInfo<TProjection> projectionTypeInfo,
        IEqualityComparer<TKey> keyEqualityComparer,
        ILogger<JsonTableIndexPersistence<TKey, TProjection>> logger)
    {
        ArgumentNullException.ThrowIfNull(keyTypeInfo);
        ArgumentNullException.ThrowIfNull(projectionTypeInfo);
        ArgumentNullException.ThrowIfNull(keyEqualityComparer);
        ArgumentNullException.ThrowIfNull(logger);

        _keyTypeInfo = keyTypeInfo;
        _projectionTypeInfo = projectionTypeInfo;
        _keyEqualityComparer = keyEqualityComparer;
        _logger = logger;
    }

    public async Task<TableIndexState<TKey, TProjection>?> LoadIfExistsAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var _ = _logger.BeginMethodScope();

        _logger.LogTrace("Loading: file=\"{Path}\"", path);

        try
        {
            var rawDto = await LoadDtoAsync(path, cancellationToken);
            var state = BuildStateFromDto(rawDto);
            _logger.LogDebug("Starting with loaded state: file=\"{Path}\" records={Records}", path, state.Records.Count);
            return state;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            _logger.LogTrace("Index file not found, starting without cached state: file=\"{Path}\"", path);
            return null;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to load index state from file: file=\"{Path}\"", path);
            return null;
        }
    }

    public byte[] SerializeToBytes(TableIndexState<TKey, TProjection> state)
    {
        ArgumentNullException.ThrowIfNull(state);

        using var _ = _logger.BeginMethodScope();

        try
        {
            var dto = BuildDtoFromState(state);
            return JsonSerializer.SerializeToUtf8Bytes(dto, IndexDtoJsonContext.Default.IndexDto);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to serialize index state to bytes");
            throw;
        }
    }

    private async Task<IndexDto?> LoadDtoAsync(string path, CancellationToken cancellationToken)
    {
        using var _ = _logger.BeginMethodScope();

        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var dto = await JsonSerializer.DeserializeAsync(fs, IndexDtoJsonContext.Default.IndexDto, cancellationToken);
        _logger.LogTrace("Loaded: file=\"{Path}\" records={Records}", path, dto?.Records.Length ?? 0);

        return dto;
    }

    private IndexDto BuildDtoFromState(TableIndexState<TKey, TProjection> state)
    {
        var dtoRecords = (
            from record in state.Records.Values
            let files = record.Files.Where(kv => kv.Value.Status != FileIndexStatus.Reserved)
                .ToDictionary(
                    kv => kv.Key,
                    kv => new FileDto(
                        kv.Value.Status == FileIndexStatus.Committed
                            ? JsonSerializer.SerializeToUtf8Bytes(kv.Value.Projection!, _projectionTypeInfo)
                            : null,
                        kv.Value.Fingerprint,
                        kv.Value.Status,
                        kv.Value.ErrorInfo,
                        kv.Value.SchemaVersion))
            where files.Count != 0
            let keyBytes = JsonSerializer.SerializeToUtf8Bytes(record.Id, _keyTypeInfo)
            select new RecordDto(keyBytes, files)
        ).ToArray();

        return new IndexDto(dtoRecords);
    }

    private TableIndexState<TKey, TProjection> BuildStateFromDto(IndexDto? dto)
    {
        using var __ = _logger.BeginMethodScope();

        var state = new TableIndexState<TKey, TProjection>(_keyEqualityComparer);

        if (dto?.Records is null)
            return state;

        foreach (var recordDto in dto.Records)
        {
            if (recordDto.Files.Count == 0)
                continue;

            var id = JsonSerializer.Deserialize(recordDto.Key, _keyTypeInfo);

            if (id is null)
            {
                _logger.LogWarning("Skipping index DTO record with null key");
                continue;
            }

            var record = state.Records.GetOrAdd(id, static key => new RecordIndexState<TKey, TProjection> { Id = key });

            foreach (var (fileName, fileDto) in recordDto.Files)
            {
                if (string.IsNullOrWhiteSpace(fileName))
                    continue;

                if (state.Files.ContainsKey(fileName))
                    continue;

                var projection = fileDto.Status == FileIndexStatus.Committed
                    ? JsonSerializer.Deserialize(fileDto.Projection, _projectionTypeInfo)
                    : default;

                var fileState = new FileIndexState<TKey, TProjection>
                {
                    Record = record,
                    Status = fileDto.Status,
                    ErrorInfo = fileDto.ErrorInfo,
                    Projection = projection,
                    Fingerprint = fileDto.Fingerprint,
                    SchemaVersion = fileDto.SchemaVersion
                };

                state.Files[fileName] = fileState;
                record.Files[fileName] = fileState;
            }
        }

        foreach (var (id, record) in state.Records)
        {
            if (record.Files.Count == 0)
            {
                state.Records.TryRemove(id, out _);
                continue;
            }

            record.RecalculateCurrent();
        }

        return state;
    }
}
