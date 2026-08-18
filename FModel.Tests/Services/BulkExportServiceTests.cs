using System.Collections.Concurrent;
using CUE4Parse.FileProvider.Objects;
using FModel.Framework;
using FModel.Services;

namespace FModel.Tests.Services;

public class BulkExportServiceTests
{
    [Fact]
    public async Task Export_ProcessesItemsConcurrentlyWithinConfiguredLimit()
    {
        using var output = new TemporaryDirectory();
        using var releaseWorkers = new ManualResetEventSlim();
        using var workersStarted = new CountdownEvent(3);
        var activeWorkers = 0;
        var enteredWorkers = 0;
        var peakWorkers = 0;
        var processed = new ConcurrentBag<string>();
        var assets = Enumerable.Range(0, 6)
            .Select(index => new FakeGameFile($"Game/Asset{index}.uasset"))
            .ToArray();
        var service = new BulkExportService(
            (asset, _, stagingRoot, _) =>
            {
                var active = Interlocked.Increment(ref activeWorkers);
                UpdateMaximum(ref peakWorkers, active);
                if (Interlocked.Increment(ref enteredWorkers) <= 3) workersStarted.Signal();
                Assert.True(releaseWorkers.Wait(TimeSpan.FromSeconds(5)));
                File.WriteAllText(Path.Combine(stagingRoot, asset.Name), asset.Path);
                processed.Add(asset.Path);
                Interlocked.Decrement(ref activeWorkers);
            },
            _ => output.Path,
            () => 3,
            StringComparer.OrdinalIgnoreCase);

        var exportTask = Task.Run(() => service.Export(assets, EBulkType.Raw, TestContext.Current.CancellationToken));

        Assert.True(workersStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(3, Volatile.Read(ref peakWorkers));
        releaseWorkers.Set();

        var summary = await exportTask;
        Assert.Equal(6, processed.Count);
        Assert.Equal(3, summary.WorkerCount);
    }

    [Fact]
    public async Task Export_LaterInputWinsWhenConflictingItemsFinishOutOfOrder()
    {
        using var output = new TemporaryDirectory();
        using var allowFirstItemToFinish = new ManualResetEventSlim();
        using var secondItemFinished = new ManualResetEventSlim();
        var assets = new[]
        {
            new FakeGameFile("Game/First.uasset"),
            new FakeGameFile("Game/Second.uasset")
        };
        var service = CreateService(output.Path, 2, (asset, _, stagingRoot, _) =>
        {
            if (asset.Name == "First.uasset")
            {
                Assert.True(allowFirstItemToFinish.Wait(TimeSpan.FromSeconds(5)));
                File.WriteAllText(Path.Combine(stagingRoot, "shared.bin"), "first");
                return;
            }

            File.WriteAllText(Path.Combine(stagingRoot, "shared.bin"), "second");
            secondItemFinished.Set();
        });

        var exportTask = Task.Run(() => service.Export(assets, EBulkType.Raw, TestContext.Current.CancellationToken));
        Assert.True(secondItemFinished.Wait(TimeSpan.FromSeconds(5)));
        allowFirstItemToFinish.Set();

        var summary = await exportTask;

        Assert.Equal("second", File.ReadAllText(Path.Combine(output.Path, "shared.bin")));
        Assert.Equal(1, summary.ExportedFileCount);
        Assert.False(Directory.Exists(Path.Combine(output.Path, ".fmodel-export-staging")));
    }

    [Fact]
    public void Export_LaterInputWinsWhenConflictingItemsFinishInOrder()
    {
        using var output = new TemporaryDirectory();
        var assets = new[]
        {
            new FakeGameFile("Game/First.uasset"),
            new FakeGameFile("Game/Second.uasset")
        };
        var service = CreateService(output.Path, 1, (asset, _, stagingRoot, _) =>
            File.WriteAllText(Path.Combine(stagingRoot, "shared.bin"), asset.Name));

        var summary = service.Export(assets, EBulkType.Raw, TestContext.Current.CancellationToken);

        Assert.Equal("Second.uasset", File.ReadAllText(Path.Combine(output.Path, "shared.bin")));
        Assert.Equal(1, summary.ExportedFileCount);
    }

    [Fact]
    public void Export_DiscardsFailedItemAndContinuesRemainingItems()
    {
        using var output = new TemporaryDirectory();
        var assets = new[]
        {
            new FakeGameFile("Game/Failed.uasset"),
            new FakeGameFile("Game/Successful.uasset")
        };
        var service = CreateService(output.Path, 2, (asset, _, stagingRoot, _) =>
        {
            File.WriteAllText(Path.Combine(stagingRoot, asset.Name), asset.Path);
            if (asset.Name == "Failed.uasset") throw new InvalidDataException("broken fixture");
        });

        var summary = service.Export(assets, EBulkType.Raw, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(output.Path, "Failed.uasset")));
        Assert.True(File.Exists(Path.Combine(output.Path, "Successful.uasset")));
        Assert.Equal(2, summary.ProcessedAssetCount);
        Assert.Equal(1, summary.FailedAssetCount);
        Assert.Equal(1, summary.ExportedFileCount);
        Assert.False(Directory.Exists(Path.Combine(output.Path, ".fmodel-export-staging")));
    }

    [Fact]
    public void Export_CancellationDiscardsCurrentItemAndCleansStagingDirectory()
    {
        using var output = new TemporaryDirectory();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var assets = new[] { new FakeGameFile("Game/Cancelled.uasset") };
        var service = CreateService(output.Path, 1, (asset, _, stagingRoot, token) =>
        {
            File.WriteAllText(Path.Combine(stagingRoot, asset.Name), asset.Path);
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
        });

        Assert.ThrowsAny<OperationCanceledException>(() => service.Export(assets, EBulkType.Raw, cancellation.Token));
        Assert.False(File.Exists(Path.Combine(output.Path, "Cancelled.uasset")));
        Assert.False(Directory.Exists(Path.Combine(output.Path, ".fmodel-export-staging")));
    }

    [Fact]
    public void Export_CancellationStopsNewItemsAndKeepsCompletedItems()
    {
        using var output = new TemporaryDirectory();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var startedItems = 0;
        var assets = Enumerable.Range(0, 4)
            .Select(index => new FakeGameFile($"Game/Asset{index}.uasset"))
            .ToArray();
        var service = CreateService(output.Path, 1, (asset, _, stagingRoot, token) =>
        {
            var started = Interlocked.Increment(ref startedItems);
            File.WriteAllText(Path.Combine(stagingRoot, asset.Name), asset.Path);
            if (started == 2)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
        });

        Assert.ThrowsAny<OperationCanceledException>(() => service.Export(assets, EBulkType.Raw, cancellation.Token));
        Assert.Equal(2, startedItems);
        Assert.True(File.Exists(Path.Combine(output.Path, "Asset0.uasset")));
        Assert.False(File.Exists(Path.Combine(output.Path, "Asset1.uasset")));
        Assert.False(File.Exists(Path.Combine(output.Path, "Asset2.uasset")));
        Assert.False(Directory.Exists(Path.Combine(output.Path, ".fmodel-export-staging")));
    }

    [Theory]
    [InlineData(EBulkType.Raw)]
    [InlineData(EBulkType.Properties)]
    [InlineData(EBulkType.Textures)]
    [InlineData(EBulkType.Meshes)]
    [InlineData(EBulkType.Animations)]
    [InlineData(EBulkType.Audio)]
    [InlineData(EBulkType.Code)]
    public void Export_SingleAndParallelWorkersProduceSameFileList(EBulkType type)
    {
        using var singleOutput = new TemporaryDirectory();
        using var parallelOutput = new TemporaryDirectory();
        var assets = Enumerable.Range(0, 6)
            .Select(index => new FakeGameFile($"Game/Folder/Asset{index}.uasset"))
            .ToArray();
        static void Processor(GameFile asset, EBulkType _, string stagingRoot, CancellationToken __)
        {
            var relativePath = Path.Combine(asset.Directory, Path.ChangeExtension(asset.Name, ".bin"));
            var outputPath = Path.Combine(stagingRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, asset.Path);
        }

        CreateService(singleOutput.Path, 1, Processor).Export(assets, type, TestContext.Current.CancellationToken);
        CreateService(parallelOutput.Path, 4, Processor).Export(assets, type, TestContext.Current.CancellationToken);

        Assert.Equal(GetRelativeFiles(singleOutput.Path), GetRelativeFiles(parallelOutput.Path));
    }

    [Theory]
    [InlineData(0, 16, 8)]
    [InlineData(0, 4, 3)]
    [InlineData(0, 1, 1)]
    [InlineData(12, 4, 12)]
    [InlineData(99, 4, 16)]
    public void ResolveWorkerCount_AppliesAutomaticAndManualLimits(int configured, int processors, int expected)
        => Assert.Equal(expected, BulkExportService.ResolveWorkerCount(configured, processors));

    private static BulkExportService CreateService(
        string outputRoot,
        int workerCount,
        BulkExportItemProcessor processor)
        => new(processor, _ => outputRoot, () => workerCount, StringComparer.OrdinalIgnoreCase);

    private static void UpdateMaximum(ref int maximum, int value)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref maximum);
            if (observed >= value) return;
        } while (Interlocked.CompareExchange(ref maximum, value, observed) != observed);
    }

    private static string[] GetRelativeFiles(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"FModel.Tests-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, true);
    }
}
