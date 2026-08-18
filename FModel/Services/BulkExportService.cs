using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.VirtualFileSystem;
using Serilog;

namespace FModel.Services;

internal delegate void BulkExportItemProcessor(GameFile asset, EBulkType type, string stagingRoot, CancellationToken cancellationToken);

internal sealed record BulkExportSummary(
    int WorkerCount,
    int AssetCount,
    int ProcessedAssetCount,
    int MatchedAssetCount,
    int ExportedFileCount,
    int FailedAssetCount,
    string OutputDirectory,
    TimeSpan Elapsed);

internal static class BulkExportExecution
{
    private static readonly AsyncLocal<State> CurrentState = new();

    public static bool IsActive => CurrentState.Value is not null;
    public static EBulkType Type => CurrentState.Value?.Type ?? EBulkType.None;

    public static string GetOutputRoot(string fallback)
        => CurrentState.Value?.StagingRoot ?? fallback;

    public static IDisposable Enter(EBulkType type, string stagingRoot)
    {
        var previous = CurrentState.Value;
        CurrentState.Value = new State(type, stagingRoot);
        return new Scope(previous);
    }

    private sealed record State(EBulkType Type, string StagingRoot);

    private sealed class Scope(State previous) : IDisposable
    {
        public void Dispose() => CurrentState.Value = previous;
    }
}

internal sealed class BulkExportService
{
    private const string StagingDirectoryName = ".fmodel-export-staging";

    private readonly BulkExportItemProcessor _processor;
    private readonly Func<EBulkType, string> _outputRootResolver;
    private readonly Func<int> _workerCountResolver;
    private readonly IEqualityComparer<string> _pathComparer;

    public BulkExportService(
        BulkExportItemProcessor processor,
        Func<EBulkType, string> outputRootResolver,
        Func<int> workerCountResolver,
        IEqualityComparer<string> pathComparer)
    {
        _processor = processor;
        _outputRootResolver = outputRootResolver;
        _workerCountResolver = workerCountResolver;
        _pathComparer = pathComparer;
    }

    public BulkExportSummary Export(IReadOnlyList<GameFile> assets, EBulkType type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assets);
        cancellationToken.ThrowIfCancellationRequested();

        var effectiveAssets = GetEffectiveAssets(assets);
        var workerCount = Math.Clamp(_workerCountResolver(), 1, 16);
        var outputRoot = Path.GetFullPath(_outputRootResolver(type));
        var batchRoot = Path.Combine(outputRoot, StagingDirectoryName, Guid.NewGuid().ToString("N"));
        var pathLocks = new ConcurrentDictionary<string, object>(_pathComparer);
        var winningIndices = new ConcurrentDictionary<string, int>(_pathComparer);
        var processedAssets = 0;
        var matchedAssets = 0;
        var failedAssets = 0;
        var stopwatch = Stopwatch.StartNew();

        Directory.CreateDirectory(batchRoot);
        try
        {
            var indexedAssets = effectiveAssets.Select(static (asset, index) => (Asset: asset, Index: index));
            var parallelOptions = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = workerCount
            };

            Parallel.ForEach(indexedAssets, parallelOptions, item =>
            {
                var itemRoot = Path.Combine(batchRoot, item.Index.ToString("D8"));
                Directory.CreateDirectory(itemRoot);
                try
                {
                    _processor(item.Asset, type, itemRoot, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    var stagedFiles = Directory.EnumerateFiles(itemRoot, "*", SearchOption.AllDirectories).ToArray();
                    if (stagedFiles.Length == 0)
                        return;

                    MergeFiles(itemRoot, outputRoot, item.Index, stagedFiles, pathLocks, winningIndices);
                    Interlocked.Increment(ref matchedAssets);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Interlocked.Increment(ref failedAssets);
                    Log.Error(exception, "Failed to export {AssetPath}", item.Asset.Path);
                }
                finally
                {
                    Interlocked.Increment(ref processedAssets);
                    TryDeleteDirectory(itemRoot);
                }
            });
        }
        finally
        {
            stopwatch.Stop();
            TryDeleteDirectory(batchRoot);
            var stagingRoot = Path.GetDirectoryName(batchRoot);
            if (stagingRoot is not null && Directory.Exists(stagingRoot) && !Directory.EnumerateFileSystemEntries(stagingRoot).Any())
                TryDeleteDirectory(stagingRoot);
        }

        return new BulkExportSummary(
            workerCount,
            effectiveAssets.Count,
            processedAssets,
            matchedAssets,
            winningIndices.Count,
            failedAssets,
            outputRoot,
            stopwatch.Elapsed);
    }

    internal static int ResolveWorkerCount(int configuredWorkerCount, int processorCount)
        => configuredWorkerCount == 0
            ? Math.Clamp(processorCount - 1, 1, 8)
            : Math.Clamp(configuredWorkerCount, 1, 16);

    private IReadOnlyList<GameFile> GetEffectiveAssets(IReadOnlyList<GameFile> assets)
    {
        var indicesByPath = new Dictionary<string, int>(_pathComparer);
        var effectiveAssets = new List<GameFile>(assets.Count);
        foreach (var asset in assets)
        {
            if (indicesByPath.TryGetValue(asset.Path, out var index))
            {
                if (GetReadOrder(asset) > GetReadOrder(effectiveAssets[index]))
                    effectiveAssets[index] = asset;
                continue;
            }

            indicesByPath[asset.Path] = effectiveAssets.Count;
            effectiveAssets.Add(asset);
        }

        return effectiveAssets;
    }

    private void MergeFiles(
        string itemRoot,
        string outputRoot,
        int itemIndex,
        IEnumerable<string> stagedFiles,
        ConcurrentDictionary<string, object> pathLocks,
        ConcurrentDictionary<string, int> winningIndices)
    {
        foreach (var stagedFile in stagedFiles)
        {
            var relativePath = Path.GetRelativePath(itemRoot, stagedFile);
            var outputPath = Path.GetFullPath(Path.Combine(outputRoot, relativePath));
            var pathLock = pathLocks.GetOrAdd(outputPath, static _ => new object());
            lock (pathLock)
            {
                if (winningIndices.TryGetValue(outputPath, out var winningIndex) && winningIndex > itemIndex)
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.Move(stagedFile, outputPath, true);
                winningIndices[outputPath] = itemIndex;
            }
        }
    }

    private static long GetReadOrder(GameFile asset)
        => asset is VfsEntry entry ? entry.Vfs.ReadOrder : 0;

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (IOException exception)
        {
            Log.Warning(exception, "Could not remove export staging directory {StagingDirectory}", path);
        }
        catch (UnauthorizedAccessException exception)
        {
            Log.Warning(exception, "Could not remove export staging directory {StagingDirectory}", path);
        }
    }
}
