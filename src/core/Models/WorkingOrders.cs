namespace SlickLadder.Core.Models;

/// <summary>
/// Our own working orders at one price: their total quantity and how many there are
/// </summary>
public readonly record struct WorkingOrders(decimal Quantity, int Count);
