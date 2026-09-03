namespace ClrSpector
{
    /// <summary>What an exception region does when control reaches it.</summary>
    /// <remarks>
    /// These are ECMA-335's own values, which is why they are not flags despite living in a
    /// field called Flags: 0 is a typed catch, and the rest are distinct kinds rather than bits
    /// that combine.
    /// </remarks>
    public enum ClrIlExceptionRegionKind
    {
        /// <summary>A typed <c>catch</c>; <see cref="ClrIlExceptionRegion.CatchTypeToken"/> says of what.</summary>
        Catch = 0x0000,

        /// <summary>A catch whose entry is decided by a filter block rather than by a type.</summary>
        Filter = 0x0001,

        /// <summary>A <c>finally</c> - runs on the way out however control leaves.</summary>
        Finally = 0x0002,

        /// <summary>A fault handler - a finally that runs only when an exception is in flight.</summary>
        Fault = 0x0004
    }

    /// <summary>
    /// One try/handler pair a method declares, as ECMA-335 records it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the same information <see cref="System.Reflection.ExceptionHandlingClause"/>
    /// carries, in a form that does not need a <see cref="System.Reflection.MethodBase"/> to
    /// exist: it is read either from a method body in the mapped image
    /// (<see cref="ClrMethodBodyImage.ExceptionRegions"/>) or converted from reflection's own
    /// clauses, so both sources of IL describe their handlers the same way.
    /// </para>
    /// <para>
    /// The offsets are IL offsets into the method body. Regions nest but never overlap, and a
    /// single try region can be named by several of these - one per handler attached to it.
    /// </para>
    /// </remarks>
    public sealed class ClrIlExceptionRegion
    {
        internal ClrIlExceptionRegion(
            ClrIlExceptionRegionKind kind,
            int tryOffset,
            int tryLength,
            int handlerOffset,
            int handlerLength,
            int filterOffset,
            uint catchTypeToken)
        {
            this.Kind = kind;
            this.TryOffset = tryOffset;
            this.TryLength = tryLength;
            this.HandlerOffset = handlerOffset;
            this.HandlerLength = handlerLength;
            this.FilterOffset = filterOffset;
            this.CatchTypeToken = catchTypeToken;
        }

        public ClrIlExceptionRegionKind Kind { get; }

        /// <summary>Where the protected region starts.</summary>
        public int TryOffset { get; }

        public int TryLength { get; }

        /// <summary>The offset just past the protected region.</summary>
        public int TryEnd => this.TryOffset + this.TryLength;

        /// <summary>Where the handler starts.</summary>
        public int HandlerOffset { get; }

        public int HandlerLength { get; }

        /// <summary>The offset just past the handler.</summary>
        public int HandlerEnd => this.HandlerOffset + this.HandlerLength;

        /// <summary>
        /// Where the filter block starts, for a <see cref="ClrIlExceptionRegionKind.Filter"/>
        /// region; zero otherwise. The filter runs before the handler and ends at it.
        /// </summary>
        public int FilterOffset { get; }

        /// <summary>
        /// The type token a typed catch names, or zero for any other kind.
        /// </summary>
        public uint CatchTypeToken { get; }

        /// <summary>
        /// The caught type's name, when whoever built this region could resolve one - from
        /// reflection's clause, or from the module's metadata. Null when it could not.
        /// </summary>
        public string CatchTypeName { get; internal set; }

        public override string ToString()
        {
            var caught = this.Kind == ClrIlExceptionRegionKind.Catch
                ? $" catch {this.CatchTypeName ?? $"0x{this.CatchTypeToken:x8}"}"
                : string.Empty;

            var filter = this.Kind == ClrIlExceptionRegionKind.Filter
                ? $" filter IL_{this.FilterOffset:x4}"
                : string.Empty;

            return $"{this.Kind} try IL_{this.TryOffset:x4}..IL_{this.TryEnd:x4} " +
                   $"handler IL_{this.HandlerOffset:x4}..IL_{this.HandlerEnd:x4}{filter}{caught}";
        }
    }
}
