using System;
using System.Collections.Generic;

namespace ClrSpectorTests;

public record ModernCircle(double Radius);

public record ModernSquare(double Side);

/// <summary>
/// A .NET 11 union. The case types are listed in the declaration's header, and the compiler
/// gives the type a <c>Value</c> of type <see cref="object"/> holding whichever case it is - so
/// matching on a union compiles to an <c>isinst</c> per case, which is what the projection shows.
/// </summary>
public union ModernShape(ModernCircle, ModernSquare);

/// <summary>
/// The constructs a modern C# method is made of, gathered so the round trip through IL can be
/// read: generic constraints, a switch expression, a match over a union, and exception filters.
/// </summary>
/// <remarks>
/// Every one of these compiles to something with no direct IL equivalent, which is the point.
/// A constraint is metadata rather than code; a switch expression is a decision tree of
/// branches; a union match is a chain of type tests; and a filter is a whole block of its own
/// that the runtime runs before it decides to catch.
/// </remarks>
public sealed class ModernLedger<T> where T : IComparable<T>, new()
{
    /// <summary>
    /// A generic method over a constrained parameter. <c>new T()</c> has no instruction of its
    /// own - the constraint makes the compiler emit a call to
    /// <see cref="Activator.CreateInstance{T}()"/> - and the comparison goes through a
    /// constrained call rather than an interface dispatch.
    /// </summary>
    public T Largest(IEnumerable<T> items)
    {
        var best = new T();

        foreach (var item in items)
            if (item.CompareTo(best) > 0)
                best = item;

        return best;
    }

    /// <summary>A switch expression over relational patterns.</summary>
    public string Classify(int n) => n switch
    {
        < 0 => "negative",
        0 => "zero",
        < 10 => "small",
        _ => "large"
    };

    /// <summary>A switch expression over a union's cases.</summary>
    public double Area(ModernShape shape) => shape switch
    {
        ModernCircle c => Math.PI * c.Radius * c.Radius,
        ModernSquare s => s.Side * s.Side,
        _ => 0
    };

    /// <summary>
    /// Two filtered catches and a finally. A <c>when</c> clause is not part of the catch in IL:
    /// it is a filter block the runtime runs first, whose result decides whether the handler
    /// runs at all.
    /// </summary>
    public int Guarded(int n)
    {
        try
        {
            throw new InvalidOperationException(n.ToString());
        }
        catch (InvalidOperationException e) when (e.Message.Length > 1)
        {
            return 1;
        }
        catch (Exception) when (n > 5)
        {
            return 2;
        }
        finally
        {
            Console.Write(string.Empty);
        }
    }
}

/// <summary>Pattern matching in the shapes source writes it.</summary>
public sealed class ModernPatterns
{
    /// <summary>Type, property, relational and combinator patterns, with a guard.</summary>
    public string Describe(object value) => value switch
    {
        null => "null",
        int i and > 100 => "big",
        int i => "int " + i.ToString(),
        string { Length: 0 } => "empty",
        string s => "string " + s.Length.ToString(),
        ModernCircle { Radius: > 10 } => "big circle",
        _ => "other"
    };

    /// <summary>List patterns, including a slice.</summary>
    public string Sequence(int[] values) => values switch
    {
        [] => "empty",
        [var only] => "one " + only.ToString(),
        [1, 2, .. var rest] => "starts " + rest.Length.ToString(),
        [.., var last] => "ends " + last.ToString()
    };

    /// <summary>Positional patterns over a tuple, and a property pattern on one.</summary>
    public string Pair((int Low, int High) range) => range switch
    {
        (0, 0) => "zero",
        var (low, high) when low > high => "inverted",
        { Low: < 0 } => "negative low",
        _ => "ordinary"
    };

    /// <summary>An is-pattern with combinators, outside a switch.</summary>
    public bool IsSmall(object value) => value is int and < 10 or short;
}
