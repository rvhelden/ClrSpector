using System.Threading.Tasks;

namespace ClrSpectorConsole;

/// <summary>
/// An await chain the continuation demonstration can park: two methods deep, suspended on a
/// gate it holds open. It lives outside <see cref="Program"/> because that class is unsafe,
/// and C# does not allow an await inside an unsafe context.
/// </summary>
internal static class AwaitChain
{
    /// <summary>What the chain suspends on.</summary>
    public static readonly TaskCompletionSource<int> Gate = new TaskCompletionSource<int>();

    public static async Task<int> AwaitsTheGate()
    {
        var value = await Gate.Task;

        return value + 1;
    }

    public static async Task<int> AwaitsTheInnerCall()
    {
        var value = await AwaitsTheGate();

        return value + 1;
    }
}