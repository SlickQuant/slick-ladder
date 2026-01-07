using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlickLadder.Core.Interop;

/// <summary>
/// JSON serialization context for snapshot data
/// Required for AOT/trimming scenarios where reflection is disabled
/// </summary>
[JsonSerializable(typeof(SnapshotJson))]
[JsonSerializable(typeof(PriceLevelJson))]
[JsonSerializable(typeof(PriceLevelJson[]))]
[JsonSerializable(typeof(MetricsJson))]
public partial class SnapshotJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Serializable snapshot data structure
/// </summary>
public class SnapshotJson
{
    public double? bestBid { get; set; }
    public double? bestAsk { get; set; }
    public double? midPrice { get; set; }
    public PriceLevelJson[] bids { get; set; } = Array.Empty<PriceLevelJson>();
    public PriceLevelJson[] asks { get; set; } = Array.Empty<PriceLevelJson>();
    public long timestamp { get; set; }
}

/// <summary>
/// Serializable price level data structure
/// </summary>
public class PriceLevelJson
{
    public double price { get; set; }
    public long quantity { get; set; }
    public int numOrders { get; set; }
    public int side { get; set; }
}

/// <summary>
/// Serializable metrics data structure
/// </summary>
public class MetricsJson
{
    public int bidLevels { get; set; }
    public int askLevels { get; set; }
    public double bestBid { get; set; }
    public double bestAsk { get; set; }
    public double? midPrice { get; set; }
    public double? spread { get; set; }
}
