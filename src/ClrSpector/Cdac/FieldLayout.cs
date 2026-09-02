namespace ClrSpector.Cdac
{
    /// <summary>
    /// One field of a runtime structure as the contract descriptor describes it.
    /// </summary>
    public readonly struct FieldLayout
    {
        public FieldLayout(int offset, string typeName)
        {
            this.Offset = offset;
            this.TypeName = typeName;
        }

        public int Offset { get; }

        /// <summary>
        /// The descriptor's name for the field's type, when it supplied one; otherwise null.
        /// Present only for fields written as <c>[offset, "typename"]</c>.
        /// </summary>
        public string TypeName { get; }
    }
}
