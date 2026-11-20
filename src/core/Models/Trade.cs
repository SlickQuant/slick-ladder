namespace SlickLadder.Core.Models;

/// <summary>
/// A trade from the feed. Aggressor is the side that crossed the spread (BID = buyer lifted the
/// ask, ASK = seller hit the bid), null when unknown. Immutable, so a snapshot can share it
/// </summary>
public sealed record Trade(decimal Price, decimal Quantity, Side? Aggressor);
