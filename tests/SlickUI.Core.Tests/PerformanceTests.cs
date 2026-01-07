using System;
using System.Diagnostics;
using SlickLadder.Core;
using SlickLadder.Core.Models;
using Xunit;

namespace SlickLadder.Core.Tests;

/// <summary>
/// Performance tests to validate <1ms latency target
/// </summary>
public class PerformanceTests
{
    [Fact]
    public void SingleUpdate_ShouldProcessInUnder1Millisecond()
    {
        // Arrange
        var core = new PriceLadderCore();
        var update = new PriceLevel(Side.BID, 100.50m, 1000, 5);

        // Warm up
        core.ProcessPriceLevelUpdate(update);
        core.Flush();
        core.Reset();

        // Act
        var sw = Stopwatch.StartNew();
        core.ProcessPriceLevelUpdate(update);
        core.Flush();
        sw.Stop();

        // Assert
        Assert.True(sw.Elapsed.TotalMilliseconds < 1.0,
            $"Update processing took {sw.Elapsed.TotalMilliseconds:F3}ms, expected <1ms");
    }

    [Fact]
    public void Batch100Updates_ShouldProcessInUnder1Millisecond()
    {
        // Arrange
        var core = new PriceLadderCore();
        var updates = new PriceLevel[100];

        for (int i = 0; i < 100; i++)
        {
            updates[i] = new PriceLevel(
                i % 2 == 0 ? Side.BID : Side.ASK,
                100m + i * 0.01m,
                1000 + i * 10,
                i + 1
            );
        }

        // Warm up
        core.ProcessBatch(updates);
        core.Flush();
        core.Reset();

        // Act
        var sw = Stopwatch.StartNew();
        core.ProcessBatch(updates);
        core.Flush();
        sw.Stop();

        // Assert
        Assert.True(sw.Elapsed.TotalMilliseconds < 1.0,
            $"Batch processing took {sw.Elapsed.TotalMilliseconds:F3}ms, expected <1ms");
    }

    [Fact]
    public void Throughput10kUpdates_ShouldProcessInUnder1Second()
    {
        // Arrange
        var core = new PriceLadderCore();
        var updates = GenerateRandomUpdates(10000);

        // Warm up
        core.ProcessBatch(new ReadOnlySpan<PriceLevel>(updates, 0, 100));
        core.Flush();
        core.Reset();

        // Act
        var sw = Stopwatch.StartNew();

        foreach (var update in updates)
        {
            core.ProcessPriceLevelUpdate(update);
        }

        core.Flush();
        sw.Stop();

        // Assert
        var metrics = core.Metrics;
        Assert.True(sw.Elapsed.TotalSeconds < 1.0,
            $"10k updates took {sw.Elapsed.TotalSeconds:F3}s, expected <1s");
        Assert.Equal(10000, metrics.TotalUpdatesProcessed);

        Console.WriteLine($"Processed {metrics.TotalUpdatesProcessed} updates in {sw.Elapsed.TotalSeconds:F3}s");
        Console.WriteLine($"Average: {metrics.AverageUpdatesPerBatch:F1} updates/batch");
        Console.WriteLine($"Throughput: {metrics.TotalUpdatesProcessed / sw.Elapsed.TotalSeconds:F0} updates/sec");
    }

    [Fact]
    public void OrderBook_BinarySearch_ShouldBeFast()
    {
        // Arrange
        var orderBook = new OrderBook();

        // Add 200 levels
        for (int i = 0; i < 100; i++)
        {
            orderBook.UpdateLevel(100m + i * 0.01m, 1000, 5, Side.BID);
            orderBook.UpdateLevel(200m + i * 0.01m, 1000, 5, Side.ASK);
        }

        // Act - measure lookup performance
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < 1000; i++)
        {
            var price = 100m + (i % 100) * 0.01m;
            orderBook.TryGetLevel(price, Side.BID, out _);
        }

        sw.Stop();

        // Assert - 1000 lookups should be very fast
        Assert.True(sw.Elapsed.TotalMicroseconds < 100,
            $"1000 lookups took {sw.Elapsed.TotalMicroseconds:F1}μs, expected <100μs");
    }

    [Fact]
    public void SortedArray_InsertAndLookup_ShouldBeFast()
    {
        // Arrange
        var array = new DataStructures.SortedArray<decimal, BookLevel>();

        // Act - insert 200 elements
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < 200; i++)
        {
            var level = new BookLevel(100m + i * 0.01m, 1000, 5, Side.BID);
            array.AddOrUpdate(level.Price, level);
        }

        sw.Stop();

        // Assert
        Assert.True(sw.Elapsed.TotalMicroseconds < 500,
            $"200 inserts took {sw.Elapsed.TotalMicroseconds:F1}μs, expected <500μs");
        Assert.Equal(200, array.Count);

        // Test lookup performance
        sw.Restart();

        for (int i = 0; i < 200; i++)
        {
            array.TryGetValue(100m + i * 0.01m, out _);
        }

        sw.Stop();

        Assert.True(sw.Elapsed.TotalMicroseconds < 50,
            $"200 lookups took {sw.Elapsed.TotalMicroseconds:F1}μs, expected <50μs");
    }

    [Fact]
    public void RingBuffer_WriteRead_ShouldBeLockFree()
    {
        // Arrange
        var buffer = new DataStructures.RingBuffer<PriceLevel>(1024);
        var update = new PriceLevel(Side.BID, 100m, 1000, 5);

        // Act - measure write/read performance
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < 1000; i++)
        {
            buffer.TryWrite(update);
            buffer.TryRead(out _);
        }

        sw.Stop();

        // Assert - lock-free operations should be very fast
        Assert.True(sw.Elapsed.TotalMicroseconds < 100,
            $"1000 write/read pairs took {sw.Elapsed.TotalMicroseconds:F1}μs, expected <100μs");
    }

    private PriceLevel[] GenerateRandomUpdates(int count)
    {
        var random = new Random(42); // Fixed seed for reproducibility
        var updates = new PriceLevel[count];

        for (int i = 0; i < count; i++)
        {
            updates[i] = new PriceLevel(
                random.Next(2) == 0 ? Side.BID : Side.ASK,
                100m + (decimal)random.NextDouble() * 10m,
                random.Next(100, 10000),
                random.Next(1, 20)
            );
        }

        return updates;
    }
}
