using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

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
        internal ClrInterfaceImplementation(uint token, HandleKind kind, string name, IntPtr methodTable)
        {
            this.Token = token;
            this.Kind = kind;
            this.Name = name;
            this.MethodTablePointer = methodTable;
        }

        /// <summary>The token naming the interface, as the implementing module sees it.</summary>
        public uint Token { get; }

        /// <summary>
        /// Whether the interface is a TypeDef (declared in this module), a TypeRef (in another),
        /// or a TypeSpec (a constructed generic).
        /// </summary>
        public HandleKind Kind { get; }

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
        public bool IsConstructedGeneric => this.Kind == HandleKind.TypeSpecification;

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
        internal static List<ClrInterfaceImplementation> Read(ClrMethodTable table)
        {
            var implementations = new List<ClrInterfaceImplementation>();

            var metadata = table.Metadata;
            if (metadata == null || table.Module == IntPtr.Zero)
                return implementations;

            var handle = MetadataTokens.EntityHandle((int)table.TypeDefToken);
            if (handle.Kind != HandleKind.TypeDefinition)
                return implementations;

            var module = ClrModule.At(table.Module);
            var definition = metadata.Reader.GetTypeDefinition((TypeDefinitionHandle)handle);

            foreach (var implementation in definition.GetInterfaceImplementations())
            {
                var declared = metadata.Reader.GetInterfaceImplementation(implementation).Interface;
                var token = (uint)MetadataTokens.GetToken(declared);

                implementations.Add(new ClrInterfaceImplementation(
                    token,
                    declared.Kind,
                    metadata.TokenName((int)token),
                    Resolve(module, declared.Kind, token)));
            }

            return implementations;
        }

        /// <summary>
        /// The MethodTable behind an interface token, using whichever of the module's lookup maps
        /// matches the token's table.
        /// </summary>
        private static IntPtr Resolve(ClrModule module, HandleKind kind, uint token)
        {
            switch (kind)
            {
                case HandleKind.TypeDefinition:
                    return module.TypeDefToMethodTable(token);

                case HandleKind.TypeReference:
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
