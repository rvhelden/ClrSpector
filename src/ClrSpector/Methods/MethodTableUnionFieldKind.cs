namespace ClrSpector
{
    /// <summary>
    /// What MethodTable.EEClassOrCanonMT points at, as encoded in the pointer's low bit.
    /// </summary>
    /// <remarks>
    /// The tag is one bit wide on .NET 10. Verified against the live runtime: every canonical
    /// type (object, string, int, int[], object[], int[,], List&lt;T&gt;, interfaces) tags 0 and
    /// its target's MethodTable back-pointer returns to itself, while shared instantiations
    /// (string[], List&lt;string&gt;, Dictionary&lt;string,int&gt;) tag 1 and point at their
    /// canonical MethodTable - string[] resolving to object[]'s.
    ///
    /// Older runtimes used a two-bit tag that also defined "invalid" (1) and "indirection" (3)
    /// kinds; neither is produced by .NET 10, and reading the tag as two bits misreads the
    /// canonical-MethodTable case as invalid.
    /// </remarks>
    public enum MethodTableUnionFieldKind
    {
        /// <summary>Points at this type's EEClass; the type is canonical.</summary>
        EEClass = 0,

        /// <summary>Points at the canonical MethodTable that owns the shared EEClass.</summary>
        MethodTable = 1
    }
}
