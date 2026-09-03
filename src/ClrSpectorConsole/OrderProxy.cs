using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ClrSpectorConsole;

/// <summary>A stand-in with state of its own, for the proxy detour.</summary>
public class OrderProxy
{
    public readonly List<string> Seen = new List<string>();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public string Describe(Order order, int wanted)
    {
        this.Seen.Add($"{order.Sku}/{wanted}");

        return "proxied";
    }
}