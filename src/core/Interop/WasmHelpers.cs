using System.Text;
using System.Text.Json;
using SlickLadder.Core.Managers;
using SlickLadder.Core.Models;

namespace SlickLadder.Core.Interop;

/// <summary>
/// Helper methods for WASM serialization and interop
/// </summary>
public static class WasmHelpers
{
    /// <summary>
    /// Serialize an OrderBookSnapshot to JSON
    /// </summary>
    public static string SerializeSnapshot(OrderBookSnapshot snapshot)
    {
        // Manual JSON serialization to avoid reflection
        var sb = new StringBuilder();
        sb.Append("{");

        // bestBid
        sb.Append("\"bestBid\":");
        sb.Append(snapshot.BestBid.HasValue ? ((double)snapshot.BestBid.Value).ToString("G17") : "null");
        sb.Append(",");

        // bestAsk
        sb.Append("\"bestAsk\":");
        sb.Append(snapshot.BestAsk.HasValue ? ((double)snapshot.BestAsk.Value).ToString("G17") : "null");
        sb.Append(",");

        // midPrice
        sb.Append("\"midPrice\":");
        sb.Append(snapshot.MidPrice.HasValue ? ((double)snapshot.MidPrice.Value).ToString("G17") : "null");
        sb.Append(",");

        // bids array
        sb.Append("\"bids\":[");
        for (int i = 0; i < snapshot.Bids.Length; i++)
        {
            if (i > 0) sb.Append(",");
            var b = snapshot.Bids[i];
            sb.Append($"{{\"price\":{(double)b.Price:G17},\"quantity\":{b.Quantity},\"numOrders\":{b.NumOrders},\"side\":{(int)b.Side}}}");
        }
        sb.Append("],");

        // asks array
        sb.Append("\"asks\":[");
        for (int i = 0; i < snapshot.Asks.Length; i++)
        {
            if (i > 0) sb.Append(",");
            var a = snapshot.Asks[i];
            sb.Append($"{{\"price\":{(double)a.Price:G17},\"quantity\":{a.Quantity},\"numOrders\":{a.NumOrders},\"side\":{(int)a.Side}}}");
        }
        sb.Append("],");

        // bidOrders (MBO mode - map of price to Order[])
        if (snapshot.BidOrders != null)
        {
            sb.Append("\"bidOrders\":{");
            bool firstBidPrice = true;
            foreach (var kvp in snapshot.BidOrders)
            {
                if (!firstBidPrice) sb.Append(",");
                firstBidPrice = false;

                sb.Append($"\"{(double)kvp.Key:G17}\":[");
                for (int i = 0; i < kvp.Value.Length; i++)
                {
                    if (i > 0) sb.Append(",");
                    var order = kvp.Value[i];
                    sb.Append($"{{\"orderId\":{order.OrderId},\"quantity\":{order.Quantity},\"priority\":{order.Priority},\"isOwnOrder\":{(order.IsOwnOrder ? "true" : "false")}}}");
                }
                sb.Append("]");
            }
            sb.Append("},");
        }

        // askOrders (MBO mode - map of price to Order[])
        if (snapshot.AskOrders != null)
        {
            sb.Append("\"askOrders\":{");
            bool firstAskPrice = true;
            foreach (var kvp in snapshot.AskOrders)
            {
                if (!firstAskPrice) sb.Append(",");
                firstAskPrice = false;

                sb.Append($"\"{(double)kvp.Key:G17}\":[");
                for (int i = 0; i < kvp.Value.Length; i++)
                {
                    if (i > 0) sb.Append(",");
                    var order = kvp.Value[i];
                    sb.Append($"{{\"orderId\":{order.OrderId},\"quantity\":{order.Quantity},\"priority\":{order.Priority},\"isOwnOrder\":{(order.IsOwnOrder ? "true" : "false")}}}");
                }
                sb.Append("]");
            }
            sb.Append("},");
        }

        // dirtyChanges (incremental render hints)
        if (snapshot.DirtyChanges != null)
        {
            sb.Append("\"dirtyChanges\":[");
            for (int i = 0; i < snapshot.DirtyChanges.Length; i++)
            {
                if (i > 0) sb.Append(",");
                var change = snapshot.DirtyChanges[i];
                sb.Append("{");
                sb.Append($"\"price\":{(double)change.Price:G17},");
                sb.Append($"\"side\":{(int)change.Side},");
                sb.Append($"\"isRemoval\":{(change.IsRemoval ? "true" : "false")},");
                sb.Append($"\"isAddition\":{(change.IsAddition ? "true" : "false")}");
                sb.Append("}");
            }
            sb.Append("],");
        }

        sb.Append("\"structuralChange\":");
        sb.Append(snapshot.StructuralChange ? "true" : "false");
        sb.Append(",");

        // timestamp
        sb.Append($"\"timestamp\":{snapshot.Timestamp.Ticks}");

        sb.Append("}");

        return sb.ToString();
    }

    /// <summary>
    /// Serialize binary update data (future optimization)
    /// </summary>
    public static byte[] SerializeUpdateBinary(PriceLevel update)
    {
        // Binary format: [side:1][price:8][quantity:4][numOrders:4] = 17 bytes
        var buffer = new byte[17];

        buffer[0] = (byte)update.Side;
        BitConverter.GetBytes((double)update.Price).CopyTo(buffer, 1);
        BitConverter.GetBytes(update.Quantity).CopyTo(buffer, 9);
        BitConverter.GetBytes(update.NumOrders).CopyTo(buffer, 13);

        return buffer;
    }

    /// <summary>
    /// Deserialize binary update data (future optimization)
    /// </summary>
    public static PriceLevel DeserializeUpdateBinary(byte[] buffer)
    {
        if (buffer.Length != 17)
            throw new ArgumentException("Invalid buffer length", nameof(buffer));

        var side = (Side)buffer[0];
        var price = (decimal)BitConverter.ToDouble(buffer, 1);
        var quantity = BitConverter.ToInt32(buffer, 9);
        var numOrders = BitConverter.ToInt32(buffer, 13);

        return new PriceLevel(side, price, quantity, numOrders);
    }
}
