using SlickLadder.Core;
using SlickLadder.Core.Models;
using System;
using System.Threading;
using SystemTimer = System.Timers.Timer;

namespace SlickLadder.Rendering.Simulation;

/// <summary>
/// Market data simulator that generates realistic price level updates.
/// Ports logic from demo.ts with intelligent batching for high update rates.
/// </summary>
public class MarketDataSimulator : IDisposable
{
    private const int TargetTotalOrders = 1500;
    private const int MaxOrdersPerLevel = 25;
    private const int MinOrderQuantity = 50;
    private const int MidOrderQuantity = 500;
    private const int MaxOrderQuantity = 20000;
    private const int LevelRemovalChancePercent = 4;

    private readonly PriceLadderCore _core;
    private SystemTimer? _timer;
    private int _updatesPerSecond = 100;
    private decimal _basePrice = 50000.00m;
    private decimal _tickSize = 0.01m;
    private readonly Random _random = new();
    private bool _useMBOMode = false;
    private volatile bool _isRunning = false;
    private int _batchInProgress;

    // MBO mode tracking
    private readonly Dictionary<decimal, List<long>> _ordersByPrice = new();
    private long _nextOrderId = 1;
    private int _totalOrders = 0;

    private enum MboAction
    {
        Add,
        Modify,
        Delete
    }

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

    public bool IsRunning => _isRunning;

    public bool UseMBOMode
    {
        get => _useMBOMode;
        set => _useMBOMode = value;
    }

    public MarketDataSimulator(PriceLadderCore core, decimal tickSize = 0.01m)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _tickSize = tickSize;
    }

    public void Start()
    {
        System.Diagnostics.Debug.WriteLine($"MarketDataSimulator.Start CALLED: _useMBOMode={_useMBOMode}, _isRunning={_isRunning}");

        if (_isRunning)
        {
            System.Diagnostics.Debug.WriteLine("MarketDataSimulator.Start: EARLY RETURN - already running");
            return;
        }

        _isRunning = true;

        if (_useMBOMode)
        {
            System.Diagnostics.Debug.WriteLine("MarketDataSimulator.Start: MBO mode - seeding orders");
            // MBO mode: Seed with individual orders
            SeedMBOOrders();
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("MarketDataSimulator.Start: PriceLevel mode - seeding levels");
            // PriceLevel mode: Seed with aggregated price levels
            _core.ProcessPriceLevelUpdateNoFlush(new PriceLevel(Side.ASK, _basePrice + _tickSize, 5000, 15));
            _core.ProcessPriceLevelUpdateNoFlush(new PriceLevel(Side.ASK, _basePrice + _tickSize * 2, 4500, 12));
            _core.ProcessPriceLevelUpdateNoFlush(new PriceLevel(Side.ASK, _basePrice + _tickSize * 3, 4000, 10));

            _core.ProcessPriceLevelUpdateNoFlush(new PriceLevel(Side.BID, _basePrice, 5000, 15));
            _core.ProcessPriceLevelUpdateNoFlush(new PriceLevel(Side.BID, _basePrice - _tickSize, 4800, 14));
            _core.ProcessPriceLevelUpdateNoFlush(new PriceLevel(Side.BID, _basePrice - _tickSize * 2, 4600, 13));
        }

        System.Diagnostics.Debug.WriteLine("MarketDataSimulator.Start: Calling Flush()");
        _core.Flush();

        var (batchSize, intervalMs) = CalculateBatchParams(_updatesPerSecond);

        _timer = new SystemTimer(intervalMs);
        _timer.Elapsed += (s, e) => GenerateBatch(batchSize);
        _timer.AutoReset = true;
        _timer.Start();
    }

    public void Stop()
    {
        if (!_isRunning) return;

        _isRunning = false;

        if (_timer != null)
        {
            _timer.Stop();
            _timer.Dispose();
            _timer = null;
        }

        if (Interlocked.CompareExchange(ref _batchInProgress, 0, 0) == 0)
        {
            _core.ClearPendingUpdates();
        }
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
        // Check if still running (prevents race condition with Stop)
        if (!_isRunning) return;

        if (Interlocked.Exchange(ref _batchInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            if (!_isRunning) return;

            if (_useMBOMode)
            {
                GenerateMBOBatch(batchSize);
            }
            else
            {
                GeneratePriceLevelBatch(batchSize);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _batchInProgress, 0);
        }
    }

    private void GeneratePriceLevelBatch(int batchSize)
    {
        for (int i = 0; i < batchSize; i++)
        {
            if (!_isRunning)
            {
                _core.ClearPendingUpdates();
                return;
            }

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

        if (!_isRunning)
        {
            _core.ClearPendingUpdates();
            return;
        }

        _core.Flush();
    }

    private void GenerateMBOBatch(int batchSize)
    {
        for (int i = 0; i < batchSize; i++)
        {
            if (!_isRunning)
            {
                _core.ClearPendingUpdates();
                return;
            }

            var hasExistingOrders = _ordersByPrice.Count > 0;
            var action = ChooseMboAction(hasExistingOrders);

            switch (action)
            {
                case MboAction.Add:
                    AddRandomOrder(hasExistingOrders);
                    break;
                case MboAction.Modify:
                    ModifyRandomOrder();
                    break;
                case MboAction.Delete:
                    DeleteRandomOrder();
                    break;
            }
        }

        if (!_isRunning)
        {
            _core.ClearPendingUpdates();
            return;
        }

        _core.Flush();
    }

    private bool HasOrdersAtPrice(decimal price)
    {
        return _ordersByPrice.TryGetValue(price, out var orders) && orders.Count > 0;
    }

    private long GetRandomOrderAtPrice(decimal price)
    {
        if (!_ordersByPrice.TryGetValue(price, out var orders) || orders.Count == 0)
        {
            return 0;
        }

        // Create snapshot to avoid race condition if list is modified
        var orderArray = orders.ToArray();
        if (orderArray.Length == 0)
        {
            return 0;
        }

        var index = _random.Next(orderArray.Length);
        return orderArray[index];
    }

    private (decimal price, long orderId) GetRandomOrder()
    {
        if (_ordersByPrice.Count == 0)
        {
            return (0, 0);
        }

        // Pick a random price level (snapshot to avoid race condition)
        var prices = _ordersByPrice.Keys.ToArray();
        if (prices.Length == 0)
        {
            return (0, 0);
        }

        var randomPrice = prices[_random.Next(prices.Length)];

        // Pick a random order at that price
        var orderId = GetRandomOrderAtPrice(randomPrice);
        return (randomPrice, orderId);
    }

    private Side GetSideForPrice(decimal price)
    {
        // Determine side based on price relative to base price
        return price >= _basePrice ? Side.ASK : Side.BID;
    }

    private void TrackOrder(long orderId, decimal price)
    {
        if (!_ordersByPrice.TryGetValue(price, out var orders))
        {
            orders = new List<long>();
            _ordersByPrice[price] = orders;
        }
        orders.Add(orderId);
        _totalOrders++;
    }

    private void UntrackOrder(long orderId, decimal price)
    {
        if (_ordersByPrice.TryGetValue(price, out var orders))
        {
            if (orders.Remove(orderId))
            {
                _totalOrders = Math.Max(0, _totalOrders - 1);
            }
            if (orders.Count == 0)
            {
                _ordersByPrice.Remove(price);
            }
        }
    }

    private void SeedMBOOrders()
    {
        // Seed asks with multiple orders per level
        for (int levelOffset = 1; levelOffset <= 3; levelOffset++)
        {
            var price = _basePrice + _tickSize * levelOffset;
            var ordersPerLevel = 10 + levelOffset * 2;

            for (int i = 0; i < ordersPerLevel; i++)
            {
                var orderId = _nextOrderId++;
                var quantity = NextOrderQuantity();
                var priority = DateTime.UtcNow.Ticks + i;
                var isOwnOrder = _random.Next(100) < 5;  // 5% chance

                _core.ProcessOrderUpdateNoFlush(
                    new OrderUpdate(orderId, Side.ASK, price, quantity, priority, isOwnOrder),
                    OrderUpdateType.Add
                );

                TrackOrder(orderId, price);
            }
        }

        // Seed bids with multiple orders per level
        for (int levelOffset = 0; levelOffset < 3; levelOffset++)
        {
            var price = _basePrice - _tickSize * levelOffset;
            var ordersPerLevel = 12 + levelOffset * 2;

            for (int i = 0; i < ordersPerLevel; i++)
            {
                var orderId = _nextOrderId++;
                var quantity = NextOrderQuantity();
                var priority = DateTime.UtcNow.Ticks + i;
                var isOwnOrder = _random.Next(100) < 5;  // 5% chance

                _core.ProcessOrderUpdateNoFlush(
                    new OrderUpdate(orderId, Side.BID, price, quantity, priority, isOwnOrder),
                    OrderUpdateType.Add
                );

                TrackOrder(orderId, price);
            }
        }
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

    private MboAction ChooseMboAction(bool hasExistingOrders)
    {
        if (!hasExistingOrders)
        {
            return MboAction.Add;
        }

        var upperBound = TargetTotalOrders + (TargetTotalOrders / 5);
        var lowerBound = TargetTotalOrders - (TargetTotalOrders / 5);

        if (_totalOrders >= upperBound)
        {
            return _random.Next(100) < 70 ? MboAction.Delete : MboAction.Modify;
        }

        if (_totalOrders <= lowerBound)
        {
            return _random.Next(100) < 70 ? MboAction.Add : MboAction.Modify;
        }

        var roll = _random.Next(100);
        if (roll < 40)
        {
            return MboAction.Add;
        }
        if (roll < 75)
        {
            return MboAction.Modify;
        }
        return MboAction.Delete;
    }

    private void AddRandomOrder(bool hasExistingOrders)
    {
        if (hasExistingOrders && _totalOrders >= TargetTotalOrders + (TargetTotalOrders / 2))
        {
            DeleteRandomOrder();
            return;
        }

        var side = _random.Next(2) == 0 ? Side.BID : Side.ASK;
        var price = FindPriceWithCapacity(side);

        if (!price.HasValue)
        {
            ModifyRandomOrder();
            return;
        }

        var orderId = _nextOrderId++;
        var quantity = NextOrderQuantity();
        var priority = DateTime.UtcNow.Ticks;
        var isOwnOrder = _random.Next(100) < 5;  // 5% chance

        _core.ProcessOrderUpdateNoFlush(
            new OrderUpdate(orderId, side, price.Value, quantity, priority, isOwnOrder),
            OrderUpdateType.Add
        );

        TrackOrder(orderId, price.Value);
    }

    private void ModifyRandomOrder()
    {
        var (price, orderId) = GetRandomOrder();
        if (orderId == 0)
        {
            return;
        }

        var newQuantity = NextOrderQuantity();
        var side = GetSideForPrice(price);

        _core.ProcessOrderUpdateNoFlush(
            new OrderUpdate(orderId, side, price, newQuantity, 0),
            OrderUpdateType.Modify
        );
    }

    private void DeleteRandomOrder()
    {
        var (price, orderId) = GetRandomOrder();
        if (orderId == 0)
        {
            return;
        }

        if (_ordersByPrice.TryGetValue(price, out var orders))
        {
            if (orders.Count <= 1 || _random.Next(100) < LevelRemovalChancePercent)
            {
                var side = GetSideForPrice(price);
                var snapshot = orders.ToArray();
                for (int i = 0; i < snapshot.Length; i++)
                {
                    var id = snapshot[i];
                    _core.ProcessOrderUpdateNoFlush(
                        new OrderUpdate(id, side, price, 0, 0),
                        OrderUpdateType.Delete
                    );
                    UntrackOrder(id, price);
                }
                return;
            }
        }

        var deleteSide = GetSideForPrice(price);
        _core.ProcessOrderUpdateNoFlush(
            new OrderUpdate(orderId, deleteSide, price, 0, 0),
            OrderUpdateType.Delete
        );

        UntrackOrder(orderId, price);
    }

    private decimal? FindPriceWithCapacity(Side side)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var price = GeneratePrice(side);
            if (!_ordersByPrice.TryGetValue(price, out var orders) || orders.Count < MaxOrdersPerLevel)
            {
                return price;
            }
        }

        foreach (var kvp in _ordersByPrice)
        {
            if (GetSideForPrice(kvp.Key) == side && kvp.Value.Count < MaxOrdersPerLevel)
            {
                return kvp.Key;
            }
        }

        return null;
    }

    private long NextOrderQuantity()
    {
        var roll = _random.Next(100);
        if (roll < 60)
        {
            return _random.Next(MinOrderQuantity, MidOrderQuantity + 1);
        }
        if (roll < 90)
        {
            return _random.Next(MidOrderQuantity, 5000);
        }
        return _random.Next(5000, MaxOrderQuantity + 1);
    }

    public void Dispose()
    {
        Stop();
    }
}
