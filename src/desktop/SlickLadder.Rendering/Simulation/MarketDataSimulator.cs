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

    public bool IsRunning => _timer != null;

    public MarketDataSimulator(PriceLadderCore core)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
    }

    public void Start()
    {
        if (_timer != null) return;

        // Initialize critical price levels to ensure they appear immediately
        // Seed first few ask levels explicitly
        _core.ProcessPriceLevelUpdateNoFlush(new PriceLevel(Side.ASK, 50000.01m, 5000, 15));
        _core.ProcessPriceLevelUpdateNoFlush(new PriceLevel(Side.ASK, 50000.02m, 4500, 12));
        _core.ProcessPriceLevelUpdateNoFlush(new PriceLevel(Side.ASK, 50000.03m, 4000, 10));

        // Seed first few bid levels explicitly
        _core.ProcessPriceLevelUpdateNoFlush(new PriceLevel(Side.BID, 50000.00m, 5000, 15));
        _core.ProcessPriceLevelUpdateNoFlush(new PriceLevel(Side.BID, 49999.99m, 4800, 14));
        _core.ProcessPriceLevelUpdateNoFlush(new PriceLevel(Side.BID, 49999.98m, 4600, 13));
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
        // Generate prices in concentrated range with enough levels to fill viewport
        // Bids: basePrice (50000.00) down to basePrice - 2.00 (49998.00)
        // Asks: basePrice + 0.01 (50000.01) to basePrice + 2.00 (50002.00)
        // This gives 200 price levels on each side - fills viewport plus scrolling buffer
        // With explicit init of 50000.01, it appears immediately

        decimal price;
        if (side == Side.BID)
        {
            // Bids: 50000.00, 49999.99, 49999.98, ... down to 49998.00
            var offset = _random.Next(0, 201) * 0.01m; // Range: 0.00 to 2.00
            price = _basePrice - offset;
        }
        else
        {
            // Asks: 50000.01, 50000.02, ... up to 50002.00 (NEVER equal to base price)
            var offset = _random.Next(1, 201) * 0.01m; // Range: 0.01 to 2.00
            price = _basePrice + offset;
        }

        return Math.Round(price, 2);
    }

    public void Dispose()
    {
        Stop();
    }
}
