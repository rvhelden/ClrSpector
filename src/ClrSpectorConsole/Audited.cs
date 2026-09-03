using System;

namespace ClrSpectorConsole;

/// <summary>How thoroughly something is audited. Byte-backed on purpose.</summary>
/// <remarks>
/// An attribute blob stores an enum argument as a bare number in the enum's underlying type and
/// says nothing about what that type is. A decoder that assumed <c>int</c> would read this one
/// three bytes too far, so the sample uses a narrow enum to show that the width is resolved from
/// the enum's own definition rather than guessed.
/// </remarks>
public enum AuditLevel : byte
{
    None = 0,
    Light = 1,
    Full = 200
}

/// <summary>Which parts of an operation are watched.</summary>
[Flags]
public enum AuditParts
{
    None = 0,
    Inputs = 1,
    Outputs = 2,
    Timing = 4
}

/// <summary>
/// An attribute reaching the argument shapes worth showing: a string, an enum, an array, a
/// <c>typeof</c>, and named arguments that set both a field and a property.
/// </summary>
[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
public sealed class AuditedAttribute : Attribute
{
    public AuditedAttribute(string reason) => this.Reason = reason;

    public AuditedAttribute(string reason, AuditLevel level)
    {
        this.Reason = reason;
        this.Level = level;
    }

    public string Reason { get; }

    public AuditLevel Level { get; }

    /// <summary>A named argument that assigns a field.</summary>
    public string Owner;

    /// <summary>A named argument that assigns a property.</summary>
    public AuditParts Parts { get; set; }

    /// <summary>A named argument holding a type, which the blob stores as a name.</summary>
    public Type Reviewer { get; set; }

    /// <summary>A named argument holding an array.</summary>
    public string[] Tags { get; set; }
}
