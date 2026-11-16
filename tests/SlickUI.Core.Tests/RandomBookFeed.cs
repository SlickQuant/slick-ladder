using System;
using System.Collections.Generic;
using System.Linq;
using SlickLadder.Core.Models;

namespace SlickLadder.Core.Tests;

/// <summary>
/// A random-rate feed around a random-walking mid. Ticks arrive with exponential gaps at a per-feed
/// rate (1-200/s), each 1-10 updates or, 2% of the time, a 50-200 update burst. Subclasses make
/// the updates. Prices are kept in ticks so the reference book compares exactly
/// </summary>
internal abstract class RandomFeed<TUpdate>
{
    public const decimal TickSize = 0.01m;
    public const int DefaultDepth = 40; // Levels per side quoted around the mid

    protected readonly Random Random;
    protected readonly int Depth;
    protected long Mid;
    private readonly double _ticksPerMs;
    private double _nextTickMs;

    /// <summary>Each level's total quantity by tick</summary>
    public Dictionary<long, decimal> Bids { get; } = new();
    public Dictionary<long, decimal> Asks { get; } = new();
    public long UpdateCount { get; protected set; }
    public decimal? BestBid => Bids.Count > 0 ? Bids.Keys.Max() * TickSize : null;
    public decimal? BestAsk => Asks.Count > 0 ? Asks.Keys.Min() * TickSize : null;

    protected RandomFeed(int seed, double? ticksPerSecond, int depth)
    {
        Random = new Random(seed);
        var randomRate = 1 + Random.NextDouble() * 199; // Drawn either way, so a seed replays the same book
        _ticksPerMs = (ticksPerSecond ?? randomRate) / 1000;
        _nextTickMs = Gap();
        Mid = 1000 + Random.Next(5_000_001);
        Depth = depth;
    }

    /// <summary>Full book on both sides</summary>
    public abstract TUpdate[] Seed();

    /// <summary>One batch per tick due by untilMs</summary>
    public List<TUpdate[]> Advance(double untilMs)
    {
        var ticks = new List<TUpdate[]>();
        for (; _nextTickMs <= untilMs; _nextTickMs += Gap())
        {
            ticks.Add(Tick());
        }
        return ticks;
    }

    protected abstract TUpdate[] Tick();

    /// <summary>Updates in a tick: 1-10, or a 50-200 burst</summary>
    protected int TickUpdateCount() => Random.NextDouble() < 0.02 ? Random.Next(50, 201) : Random.Next(1, 11);

    private double Gap() => -Math.Log(1 - Random.NextDouble()) / _ticksPerMs;
}

/// <summary>
/// Price level updates: a quantity per level, 10% of them clearing it
/// </summary>
internal sealed class RandomBookFeed : RandomFeed<PriceLevel>
{
    public RandomBookFeed(int seed, double? ticksPerSecond = null, int depth = DefaultDepth)
        : base(seed, ticksPerSecond, depth)
    {
    }

    public override PriceLevel[] Seed()
    {
        var batch = new List<PriceLevel>(Depth * 2);
        for (var distance = 1; distance <= Depth; distance++)
        {
            batch.Add(Level(Side.BID, Mid - distance, Random.Next(1, 10001)));
            batch.Add(Level(Side.ASK, Mid + distance, Random.Next(1, 10001)));
        }
        return batch.ToArray();
    }

    protected override PriceLevel[] Tick()
    {
        var batch = new List<PriceLevel>();
        if (Random.NextDouble() < 0.1)
        {
            // Keeps bids < mid < asks: clear the level the mid moves onto
            Mid += Random.NextDouble() < 0.5 ? -1 : 1;
            batch.Add(Level(Side.BID, Mid, 0));
            batch.Add(Level(Side.ASK, Mid, 0));
        }
        var count = TickUpdateCount();
        for (var i = 0; i < count; i++)
        {
            var side = Random.NextDouble() < 0.5 ? Side.BID : Side.ASK;
            var distance = Random.Next(1, Depth + 1);
            var quantity = Random.NextDouble() < 0.1 ? 0 : Random.Next(1, 10001);
            batch.Add(Level(side, side == Side.BID ? Mid - distance : Mid + distance, quantity));
        }
        return batch.ToArray();
    }

    private PriceLevel Level(Side side, long tick, decimal quantity)
    {
        var book = side == Side.BID ? Bids : Asks;
        if (quantity > 0) book[tick] = quantity; else book.Remove(tick);
        UpdateCount++;
        return new PriceLevel(side, tick * TickSize, quantity, quantity > 0 ? Random.Next(1, 31) : 0);
    }
}
