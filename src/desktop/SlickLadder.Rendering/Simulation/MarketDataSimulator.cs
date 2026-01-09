using SlickLadder.Core;
using SlickLadder.Core.Models;
using System;
using SystemTimer = System.Timers.Timer;

namespace SlickLadder.Rendering.Simulation;

/// <summary>
/// Market data simulator that generates realistic price level updates.
/// Ports logic from demo.ts with intelligent batching for high update rates.
/// </summary>
public class MarketDataSimulator : IDisposable
{
    private readonly PriceLadderCore _core;
    private SystemTimer? _timer;
    private int _updatesPerSecond = 100;
    private decimal _basePrice = 50000.00m;
    private decimal _tickSize = 0.01m;
    private readonly Random _random = new();

    public int UpdatesPerSecond
    {
        get => _updatesPerSecond;
        set
        {
            _updatesPerSecond = value;
            if (_timer != null)
            {
                Restart();
            }
        }
    }

    public decimal BasePrice
    {
        get => _basePrice;
        set => _basePrice = value;
    }

    public decimal TickSize
    {
        get => _tickSize;
        set => _tickSize = value;
    }

    public bool IsRunning => _timer != null;

    public MarketDataSimulator(PriceLadderCore core, decimal tickSize = 0.01m)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _tickSize = tickSize;
    }

    public void Start()
    {
        if (_timer != null) return;

        // Initialize critical price levels to ensure they appear immediately
        // Seed first few ask levels explicitly using tick size
        _core.ProcessPriceLevelUpdateNoFlush(new PriceLevel(Side.ASK, _basePrice + _tickSize, 5000, 15));
        _core.ProcessPriceLevelUpdateNoFlush(new PriceLevel(Side.ASK, _basePrice + _tickSize * 2, 4500, 12));
        _core.ProcessPriceLevelUpdateNoFlush(new PriceLevel(Side.ASK, _basePrice + _tickSize * 3, 4000, 10));

        // Seed first few bid levels explicitly using tick size
        _core.ProcessPriceLevelUpdateNoFlush(new PriceLevel(Side.BID, _basePrice, 5000, 15));
        _core.ProcessPriceLevelUpdateNoFlush(new PriceLevel(Side.BID, _basePrice - _tickSize, 4800, 14));
        _core.ProcessPriceLevelUpdateNoFlush(new PriceLevel(Side.BID, _basePrice - _tickSize * 2, 4600, 13));
        _core.Flush();

        var (batchSize, intervalMs) = CalculateBatchParams(_updatesPerSecond);

        _timer = new SystemTimer(intervalMs);
        _timer.Elapsed += (s, e) => GenerateBatch(batchSize);
        _timer.AutoReset = true;
        _timer.Start();
    }

    public void Stop()
    {
        if (_timer == null) return;

        _timer.Stop();
        _timer.Dispose();
        _timer = null;
    }

    private void Restart()
    {
        Stop();
        Start();
    }

    /// <summary>
    /// Calculate batch size and interval based on target update rate.
    /// Matches logic from demo.ts for intelligent batching.
    /// </summary>
    private (int batchSize, double intervalMs) CalculateBatchParams(int rate)
    {
        if (rate <= 100)
        {
            // Low rate: send individual updates
            return (1, 1000.0 / rate);
        }
        else if (rate <= 1000)
        {
            // Medium rate: batch at 100 Hz
            return ((int)Math.Ceiling(rate / 100.0), 10.0);
        }
        else
        {
            // High rate: batch at ~60 Hz
            return ((int)Math.Ceiling(rate / 60.0), 16.67);
        }
    }

    private void GenerateBatch(int batchSize)
    {
        for (int i = 0; i < batchSize; i++)
        {
            var side = _random.Next(2) == 0 ? Side.BID : Side.ASK;
            var price = GeneratePrice(side);

            // Occasionally remove levels (set qty = 0) to simulate level removal
            // 5% chance of removal
            long qty;
            int numOrders;
            if (_random.Next(100) < 5)
            {
                qty = 0;
                numOrders = 0;
            }
            else
            {
                qty = _random.Next(100, 10000);
                numOrders = _random.Next(1, 30);
            }

            _core.ProcessPriceLevelUpdateNoFlush(new PriceLevel(side, price, qty, numOrders));
        }

        _core.Flush();
    }

    private decimal GeneratePrice(Side side)
    {
        // Generate prices using the configured tick size
        // Use basePrice as reference to match viewport center
        decimal referencePrice = _basePrice;

        decimal price;
        if (side == Side.BID)
        {
            // Generate bids around basePrice, going down
            // Pick a level from 0 to 50 ticks below reference
            var ticksBelow = _random.Next(0, 51);
            price = referencePrice - ticksBelow * _tickSize;
        }
        else
        {
            // Generate asks around basePrice + tickSize, going up
            // Pick a level from 1 to 50 ticks above reference
            var ticksAbove = _random.Next(1, 51);
            price = referencePrice + ticksAbove * _tickSize;
        }

        // Round to tick size to avoid floating-point precision issues
        return Math.Round(price / _tickSize) * _tickSize;
    }

    public void Dispose()
    {
        Stop();
    }
}
