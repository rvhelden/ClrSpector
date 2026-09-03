using System;
using System.Reflection;

namespace ClrSpector
{
    /// <summary>
    /// One of a method's local variable slots: what it holds, and whether the GC has to leave it
    /// where it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A method body does not carry its locals - it carries a token for a standalone signature
    /// that describes them, which is why locals are the last part of a body to become readable.
    /// This is that signature decoded, or reflection's own
    /// <see cref="LocalVariableInfo"/> converted, so IL from either source describes its slots
    /// the same way.
    /// </para>
    /// <para>
    /// The two sources name a type differently and there is no honest way to make them agree:
    /// reflection resolves it to a <see cref="System.Type"/>, the signature reader to a name and
    /// a shape. Both are kept, and <see cref="TypeName"/> is whichever of them there is.
    /// </para>
    /// </remarks>
    public sealed class ClrIlLocal
    {
        private ClrIlLocal(int index)
        {
            this.Index = index;
        }

        /// <summary>The slot number the IL loads and stores by.</summary>
        public int Index { get; }

        /// <summary>
        /// True when the slot is pinned: the GC may not move what it points at while it lives.
        /// </summary>
        public bool IsPinned { get; private set; }

        /// <summary>True when the slot holds a managed reference rather than a value.</summary>
        public bool IsByRef { get; private set; }

        /// <summary>The type, when the IL came from reflection; null otherwise.</summary>
        public Type Type { get; private set; }

        /// <summary>
        /// The type as its signature describes it, when the IL was read from a module image;
        /// null otherwise. Pinning and by-ref are unwrapped into
        /// <see cref="IsPinned"/> and <see cref="IsByRef"/>, so this is the type underneath them.
        /// </summary>
        public ClrSignatureType SignatureType { get; private set; }

        /// <summary>The type's name from whichever source described it, or null when neither did.</summary>
        public string TypeName { get; private set; }

        /// <summary>Reflection's view of a local.</summary>
        internal static ClrIlLocal Of(LocalVariableInfo local)
        {
            var type = local.LocalType;

            return new ClrIlLocal(local.LocalIndex)
            {
                IsPinned = local.IsPinned,
                IsByRef = type?.IsByRef ?? false,
                Type = type,
                TypeName = type?.FullName ?? type?.Name
            };
        }

        /// <summary>A local decoded from a LocalVarSig blob.</summary>
        internal static ClrIlLocal Of(int index, ClrSignatureType type)
        {
            var local = new ClrIlLocal(index);

            // Pinning and by-ref are modifiers wrapped around the type in the signature, and
            // either can be outermost, so both are peeled off before the type is recorded.
            while (type != null)
            {
                if (type.ElementType == CorElementType.PINNED)
                {
                    local.IsPinned = true;
                    type = type.Element;

                    continue;
                }

                if (type.ElementType == CorElementType.BYREF)
                {
                    local.IsByRef = true;
                    type = type.Element;

                    continue;
                }

                break;
            }

            local.SignatureType = type;
            local.TypeName = type?.ToString();

            return local;
        }

        public override string ToString()
        {
            var pinned = this.IsPinned ? " pinned" : string.Empty;
            var reference = this.IsByRef && this.SignatureType != null ? "ref " : string.Empty;

            return $"[{this.Index}] {reference}{this.TypeName ?? "?"}{pinned}";
        }
    }
}
