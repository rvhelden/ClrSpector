using System;
using System.Runtime.CompilerServices;

namespace ClrSpectorConsole;

/// <summary>A small type to point everything at.</summary>
public class Order : IPriced, IComparable<Order>
{
    public int Quantity = 3;

    public decimal UnitPrice = 2.5m;

    public string Sku = "A-1";

    [MethodImpl(MethodImplOptions.NoInlining)]
    public decimal Total() => this.Quantity * this.UnitPrice;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public string Describe(int wanted) => wanted > this.Quantity ? "short" : "ok";

    [MethodImpl(MethodImplOptions.NoInlining)]
    public virtual string Ship() => "shipped";

    /// <summary>Enough shape to be worth projecting back to C#: a loop, a branch, a catch.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public string Restock(int wanted)
    {
        var missing = 0;

        for (var i = 0; i < wanted; i++)
            missing += i < this.Quantity ? 0 : 1;

        try
        {
            return missing == 0 ? "ok" : "short " + missing;
        }
        catch (InvalidOperationException)
        {
            return "failed";
        }
    }

    public int CompareTo(Order other) => 0;
}