using System;
using System.Collections.Generic;

namespace ClrSpector
{
    /// <summary>
    /// One interface a type declares it implements.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The runtime builds a full interface map on every MethodTable, but the contract publishes
    /// only <see cref="ClrMethodTable.NumberOfInterfaces"/> - the count - and no pointer to the
    /// map itself. The runtime's own reader has the same limitation. So the interfaces are read
    /// from <b>metadata</b> instead, which lists what each type declares.
    /// </para>
    /// <para>
    /// That difference is worth knowing. Metadata lists what a type's own row declares; the count
    /// on the MethodTable is the runtime's closure. For a class the C# compiler already writes the
    /// closure of its <i>own</i> interfaces into metadata, so the two usually agree - but a class
    /// that inherits an interface from its <b>base class</b> declares nothing itself, and there
    /// the counts diverge: measured, a derived class declared 0 where the runtime counted 1.
    /// </para>
    /// </remarks>
    public sealed class ClrInterfaceImplementation
    {
        /// <summary>A token's low three bytes are its row id.</summary>
        private const uint RowIdMask = 0x00FFFFFF;

        internal ClrInterfaceImplementation(
            uint token, MetadataTable typeTable, string name, IntPtr methodTable)
        {
            this.Token = token;
            this.TypeTable = typeTable;
            this.Name = name;
            this.MethodTablePointer = methodTable;
        }

        /// <summary>The token naming the interface, as the implementing module sees it.</summary>
        public uint Token { get; }

        /// <summary>
        /// Whether the interface is a TypeDef (declared in this module), a TypeRef (in another),
        /// or a TypeSpec (a constructed generic).
        /// </summary>
        public MetadataTable TypeTable { get; }

        /// <summary>The interface's name.</summary>
        public string Name { get; }

        /// <summary>
        /// The interface's own MethodTable, or <see cref="IntPtr.Zero"/> when it could not be
        /// reached.
        /// </summary>
        /// <remarks>
        /// A TypeDef or TypeRef resolves through the module's own lookup maps. A constructed
        /// generic does not: its MethodTable is built per instantiation and is in neither map, so
        /// it is named but not reached.
        /// </remarks>
        public IntPtr MethodTablePointer { get; }

        /// <summary>True for a constructed generic interface, which has no MethodTable here.</summary>
        public bool IsConstructedGeneric => this.TypeTable == MetadataTable.TypeSpec;

        /// <summary>
        /// The decoded interface - its methods, its fields, and which of its methods carry a
        /// default implementation. Null when <see cref="MethodTablePointer"/> is zero.
        /// </summary>
        public ClrMethodTable Interface =>
            this.MethodTablePointer == IntPtr.Zero
                ? null
                : ClrMethodTable.Create(new MemoryReader(this.MethodTablePointer));

        /// <summary>
        /// Reads the interfaces <paramref name="table"/>'s type declares.
        /// </summary>
        /// <remarks>
        /// The InterfaceImpl table lists implementing-type to interface pairs. ECMA-335 requires
        /// it sorted by the implementing type, so the rows for one type are a contiguous run that
        /// a binary search can find - but a writer is only obliged to say whether it sorted, so
        /// that claim is checked and an unsorted table is scanned instead.
        /// </remarks>
        internal static List<ClrInterfaceImplementation> Read(ClrMethodTable table)
        {
            var implementations = new List<ClrInterfaceImplementation>();

            var metadata = table.Metadata;
            if (metadata == null || table.Module == IntPtr.Zero)
                return implementations;

            if ((MetadataTable)(table.TypeDefToken >> 24) != MetadataTable.TypeDef)
                return implementations;

            var image = metadata.Image;
            var typeRowId = table.TypeDefToken & RowIdMask;
            var rowCount = (uint)image.RowCount(MetadataTable.InterfaceImpl);

            if (typeRowId == 0 || rowCount == 0)
                return implementations;

            var module = ClrModule.At(table.Module);
            var first = image.IsSorted(MetadataTable.InterfaceImpl)
                ? FirstSortedRow(image, typeRowId, rowCount)
                : 1;

            for (var rowId = first; rowId <= rowCount; rowId++)
            {
                // InterfaceImpl: Class (a TypeDef row id), Interface (a TypeDefOrRef).
                var owner = image.ReadColumn(MetadataTable.InterfaceImpl, rowId, 0);

                if (owner != typeRowId)
                {
                    // On a sorted table the run has ended; otherwise keep looking.
                    if (image.IsSorted(MetadataTable.InterfaceImpl) && owner > typeRowId)
                        break;

                    continue;
                }

                var declared = image.DecodeCoded(
                    CodedIndex.TypeDefOrRef,
                    image.ReadColumn(MetadataTable.InterfaceImpl, rowId, 1));

                var token = ((uint)declared.Table << 24) | declared.RowId;

                implementations.Add(new ClrInterfaceImplementation(
                    token,
                    declared.Table,
                    metadata.TokenName((int)token),
                    Resolve(module, declared.Table, token)));
            }

            return implementations;
        }

        /// <summary>
        /// The first row of the run belonging to <paramref name="typeRowId"/>, or one past the
        /// table when there is none.
        /// </summary>
        private static uint FirstSortedRow(MetadataImage image, uint typeRowId, uint rowCount)
        {
            uint low = 1;
            var high = rowCount;
            var found = rowCount + 1;

            while (low <= high)
            {
                var middle = low + ((high - low) / 2);
                var owner = image.ReadColumn(MetadataTable.InterfaceImpl, middle, 0);

                if (owner >= typeRowId)
                {
                    found = middle;

                    if (middle == 1)
                        break;

                    high = middle - 1;
                }
                else
                {
                    low = middle + 1;
                }
            }

            return found;
        }

        /// <summary>
        /// The MethodTable behind an interface token, using whichever of the module's lookup maps
        /// matches the token's table.
        /// </summary>
        private static IntPtr Resolve(ClrModule module, MetadataTable table, uint token)
        {
            switch (table)
            {
                case MetadataTable.TypeDef:
                    return module.TypeDefToMethodTable(token);

                case MetadataTable.TypeRef:
                    return module.TypeRefToMethodTable(token);

                default:
                    return IntPtr.Zero;
            }
        }

        public override string ToString()
        {
            var located = this.MethodTablePointer == IntPtr.Zero
                ? this.IsConstructedGeneric ? " (constructed generic - no MethodTable here)" : " (not loaded)"
                : $" @0x{this.MethodTablePointer.ToInt64():x}";

            return $"{this.Name}{located}";
        }
    }
}