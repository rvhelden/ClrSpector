namespace ClrSpectorConsole;

/// <summary>An interface with one abstract member and one default implementation.</summary>
public interface IPriced
{
    decimal Total();

    /// <summary>A default implementation - a body on the interface itself.</summary>
    string Summary() => $"total {this.Total()}";
}