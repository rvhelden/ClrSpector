using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ClrSpector;
using ClrSpector.Cdac;

namespace ClrSpectorConsole
{
    [StructLayout(LayoutKind.Sequential)]
    public struct TestStruct
    {
        public readonly byte Test1;
        public readonly byte Test2;
        public readonly byte Test3;
        public readonly byte Test4;
    }

    internal static class Program
    {
        /// <summary>How many recognisable objects the heap dump allocates before walking.</summary>
        private const int MarkerCount = 500;

        /// <summary>How many instances of each type the heap dump shows the value of.</summary>
        private const int SampleLimit = 3;

        /// <summary>How many of the heap's types the dump shows, largest first.</summary>
        private const int TypeLimit = 5;

        /// <summary>Fields and elements shown per sampled object, and the width of each value.</summary>
        private const int MembersPerSample = 3;

        private const int ValueWidth = 256;

        /// <summary>
        /// The running totals for one type on the heap, plus the first few instances of it.
        /// </summary>
        /// <remarks>
        /// Only the address, size and element count are kept during the walk. Reading the values
        /// happens afterwards, because that means resolving types and formatting strings, and
        /// doing that inside the walk would allocate against the very no-GC budget the walk
        /// depends on - and push the allocation buffers it is stepping around.
        /// </remarks>
        private sealed class TypeSummary
        {
            public int Count { get; private set; }

            public long Bytes { get; private set; }

            public List<(IntPtr Address, long Size, uint ComponentCount)> Samples { get; } =
                new List<(IntPtr, long, uint)>(SampleLimit);

            public void Add(ClrHeapObject instance)
            {
                this.Count++;
                this.Bytes += instance.Size;

                if (this.Samples.Count < SampleLimit)
                    this.Samples.Add((instance.Address, instance.Size, instance.ComponentCount));
            }
        }

        private static void Main()
        {
            PrintRuntime();
            
            foreach (var type in new[]
                     {
                         typeof(SampleClass),
                         typeof(TestStruct),
                         typeof(string),
                         typeof(int[]),
                         typeof(List<int>),
                         typeof(List<string>)
                     })
            {
                Dump(type);
            }

            DumpHeap();
        }

        /// <summary>
        /// Prints the generation and segment structure, then a by-type summary of the objects the
        /// walk finds - the eeheap and dumpheap views.
        /// </summary>
        /// <remarks>
        /// The walked byte total is printed next to what the segments themselves report, for the
        /// same reason the type dump prints the reflection view alongside the decoded one: if the
        /// walk were stopping early, the two numbers would visibly disagree. The count of a
        /// population allocated here is the same idea - it is checked, not assumed.
        /// </remarks>
        private static void DumpHeap()
        {
            Console.WriteLine();
            Console.WriteLine(new string('=', 78));

            // A population to look for, so the summary has something recognisable in it.
            var markers = new List<SampleClass>();
            for (var i = 0; i < MarkerCount; i++)
                markers.Add(new SampleClass(i));

            // The scope is entered before the heap is read: establishing it collects, which moves
            // objects and rebuilds the region lists, so an earlier snapshot would be stale.
            using var scope = GcWalkScope.Enter();
            var heap = ClrGcHeap.Refresh();
            var gc = heap.Layouts.Gc;

            Console.WriteLine($"GC           : {heap.Identifiers}");
            Console.WriteLine($"Descriptor   : GC contract v{gc.Contracts["GC"]}, {gc.TypeNames.Count()} types, " +
                              $"{gc.Globals.Names.Count()} globals (found by scanning - it is not exported)");
            Console.WriteLine($"Address range: 0x{heap.Layouts.LowestAddress:x} - 0x{heap.Layouts.HighestAddress:x}");
            Console.WriteLine($"Collection   : held off = {scope.IsProtected}");

            Console.WriteLine();
            Console.WriteLine("Generations and segments");

            long reported = 0;
            foreach (var generation in heap.Generations)
            {
                Console.WriteLine($"  {generation}");
                foreach (var segment in generation.Segments)
                {
                    reported += segment.LiveBytes;
                    var kind = segment.IsReadOnly ? " frozen"
                        : segment.IsEphemeral ? " ephemeral"
                        : string.Empty;

                    Console.WriteLine($"     0x{segment.Mem.ToInt64():x}  live {segment.LiveBytes,9}  " +
                                      $"committed {segment.CommittedBytes,9}  reserved {segment.ReservedBytes,10}  " +
                                      $"flags 0x{segment.Flags:x}{kind}");
                }
            }

            var byType = new Dictionary<IntPtr, TypeSummary>();
            var walked = 0;
            long walkedBytes = 0;
            var free = 0;
            long freeBytes = 0;
            var declined = 0;

            foreach (var segment in heap.Segments)
            {
                // A segment the walk will not decode raises rather than guessing. Counted and
                // reported here rather than abandoning the whole dump.
                try
                {
                    foreach (var instance in heap.EnumerateObjects(segment, scope))
                    {
                        walked++;
                        walkedBytes += instance.Size;

                        if (instance.IsFree)
                        {
                            free++;
                            freeBytes += instance.Size;
                            continue;
                        }

                        if (!byType.TryGetValue(instance.MethodTablePointer, out var summary))
                        {
                            summary = new TypeSummary();
                            byType.Add(instance.MethodTablePointer, summary);
                        }

                        summary.Add(instance);
                    }
                }
                catch (ClrSpectorUnsupportedRuntimeException)
                {
                    declined++;
                }
            }

            var coverage = reported > 0 ? walkedBytes * 100 / reported : 0;

            Console.WriteLine();
            Console.WriteLine($"Objects      : {walked} in {walkedBytes} bytes - {coverage}% of the " +
                              $"{reported} the segments report");
            Console.WriteLine($"Free space   : {free} fillers in {freeBytes} bytes");
            Console.WriteLine($"Declined     : {declined} segment(s) the walk would not decode");
            Console.WriteLine(
                $"Collected    : {scope.CollectionOccurred} - a walk a collection ran through is unreliable");

            Console.WriteLine();
            Console.WriteLine($"Largest {TypeLimit} of the {byType.Count} types on the heap, " +
                              $"with up to {SampleLimit} instances each");
            Console.WriteLine();

            // Rendered while the scope is still open, so nothing has moved since the addresses
            // were read. If a collection has slipped through, the addresses are stale and the
            // values would be nonsense, so only the counts are printed.
            var stale = scope.CollectionOccurred;

            foreach (var entry in byType.OrderByDescending(e => e.Value.Bytes).Take(TypeLimit))
            {
                Console.WriteLine($"  {entry.Value.Count,7} objects  {entry.Value.Bytes,10} bytes  " +
                                  $"{NameOf(entry.Key)}");

                foreach (var sample in entry.Value.Samples)
                {
                    var shape = sample.ComponentCount > 0 ? $" count={sample.ComponentCount}" : string.Empty;
                    var value = stale ? "<a collection intervened>" : Render(sample.Address);

                    Console.WriteLine($"            0x{sample.Address.ToInt64():x}  {sample.Size,6} bytes{shape}" +
                                      $"  {value}");
                }
            }

            // The independent check: a known number was allocated above, so that many must be
            // found. A walk that stops early reports a plausible number here instead.
            byType.TryGetValue(typeof(SampleClass).TypeHandle.Value, out var markerTotals);

            Console.WriteLine();
            Console.WriteLine($"SampleClass  : allocated {MarkerCount}, walk found {markerTotals.Count} " +
                              $"in {markerTotals.Bytes} bytes");

            GC.KeepAlive(markers);
        }

        /// <summary>
        /// A readable name for a MethodTable found on the heap, falling back to its address when
        /// the type cannot be resolved - the runtime's Free type has no metadata to resolve.
        /// </summary>
        private static string NameOf(IntPtr methodTable)
        {
            try
            {
                var type = Type.GetTypeFromHandle(RuntimeTypeHandle.FromIntPtr(methodTable));
                return type == null ? $"0x{methodTable.ToInt64():x}" : FriendlyName(type);
            }
            catch (Exception)
            {
                return $"0x{methodTable.ToInt64():x} <unresolved>";
            }
        }

        /// <summary>
        /// The value of the object at <paramref name="address"/>, as one short readable line.
        /// </summary>
        /// <remarks>
        /// The walk yields addresses, not references, so reading a value means turning one back
        /// into a reference. A reference *is* an address - reinterpreting the bytes is exactly
        /// what the runtime holds in a local - so once that is done it is an ordinary reference
        /// and reflection can read the fields, with no offset decoding at all.
        ///
        /// That is only sound because the walk has already established this is an object start
        /// whose MethodTable is mapped. Handing the GC a reference to something that is not an
        /// object corrupts the heap at the next collection, so free-space fillers - which have
        /// no type to resolve - are never passed here, and neither should anything unvalidated
        /// be. It is also why this is done inside the walk's scope, while nothing is moving.
        /// </remarks>
        private static string Render(IntPtr address)
        {
            try
            {
                var instance = ReferenceAt(address);

                return instance switch
                {
                    null => "null",
                    string text => Quote(text),
                    Array array => RenderArray(array),
                    _ => RenderFields(instance)
                };
            }
            catch (Exception error)
            {
                return $"<{error.GetType().Name}>";
            }
        }

        /// <summary>Reinterprets a heap address as the object reference it is.</summary>
        private static unsafe object ReferenceAt(IntPtr address)
        {
            return Unsafe.Read<object>(&address);
        }

        private static string RenderArray(Array array)
        {
            if (array.Rank != 1)
                return $"rank {array.Rank}, {array.Length} elements";

            if (array.Length == 0)
                return "[]";

            var shown = Math.Min(array.Length, MembersPerSample);
            var elements = new List<string>(shown);

            for (var i = 0; i < shown; i++)
            {
                try
                {
                    elements.Add(Scalar(array.GetValue(i)));
                }
                catch (Exception)
                {
                    elements.Add("?");
                }
            }

            var ellipsis = array.Length > shown ? ", …" : string.Empty;
            return $"[{string.Join(", ", elements)}{ellipsis}]";
        }

        /// <summary>
        /// The first few instance fields of an object, as name=value pairs.
        /// </summary>
        /// <remarks>
        /// Reflection reads the fields rather than the field offsets being decoded from the
        /// EEClass, which the inspector does not walk. The point here is to show that the object
        /// the walk found really is the object it claims to be.
        /// </remarks>
        private static string RenderFields(object instance)
        {
            var type = instance.GetType();
            var fields = type
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Take(MembersPerSample)
                .ToList();

            if (fields.Count == 0)
                return "{}";

            var rendered = new List<string>(fields.Count);
            foreach (var field in fields)
            {
                try
                {
                    rendered.Add($"{field.Name}={Scalar(field.GetValue(instance))}");
                }
                catch (Exception)
                {
                    rendered.Add($"{field.Name}=?");
                }
            }

            return "{ " + string.Join(", ", rendered) + " }";
        }

        /// <summary>
        /// One field or element value.
        /// </summary>
        /// <remarks>
        /// Only types whose ToString is known to be cheap and total are formatted. Anything else
        /// gets its type name: calling an arbitrary ToString here could allocate heavily, throw,
        /// or recurse, and this runs while collection is being held off.
        /// </remarks>
        private static string Scalar(object value)
        {
            switch (value)
            {
                case null:
                    return "null";

                case string text:
                    return Quote(text);

                case decimal or DateTime or DateTimeOffset or TimeSpan or Guid:
                    return value.ToString();
            }

            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum)
                return value.ToString();

            return "{" + FriendlyName(type) + "}";
        }

        /// <summary>A string value, quoted, with control characters and length made visible.</summary>
        private static string Quote(string text)
        {
            var length = text.Length;
            var body = length > ValueWidth ? text.Substring(0, ValueWidth) : text;

            body = body
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");

            var ellipsis = length > ValueWidth ? "…" : string.Empty;
            return $"\"{body}{ellipsis}\" ({length} chars)";
        }

        private static void PrintRuntime()
        {
            var descriptor = ClrObject.Descriptor;

            Console.WriteLine(
                $"Runtime      : {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.RuntimeIdentifier})");
            Console.WriteLine($"CoreLib      : {typeof(object).Assembly.Location}");
            Console.WriteLine($"Descriptor   : version {descriptor.Version}, baseline '{descriptor.Baseline}', " +
                              $"{descriptor.TypeNames.Count()} types, {descriptor.Globals.Names.Count()} globals");
            Console.WriteLine($"Architecture : {descriptor.Globals.Text("Architecture")}");
        }

        /// <summary>
        /// Prints the decoded runtime view of a type next to the reflection view of the same
        /// type, so a decoding mistake shows up as a visible disagreement.
        /// </summary>
        private static void Dump(Type type)
        {
            Console.WriteLine();
            Console.WriteLine(new string('-', 78));
            Console.WriteLine(type.FullName);

            var methodTable = ClrObject.From(type).MethodTable;
            var eeClass = methodTable.EEClass;

            Console.WriteLine($"  MethodTable   0x{methodTable.Address.ToInt64():x}");
            Console.WriteLine($"  baseSize      {methodTable.BaseSize}");
            Console.WriteLine($"  virtuals      {methodTable.NumberOfVirtuals}");
            Console.WriteLine($"  interfaces    {methodTable.NumberOfInterfaces}");
            Console.WriteLine($"  category      class={methodTable.IsClass} valueType={methodTable.IsValueType} " +
                              $"interface={methodTable.IsInterface} array={methodTable.IsArray}");

            if (methodTable.HasComponentSize)
                Console.WriteLine($"  componentSize {methodTable.ComponentSize}");

            Console.WriteLine(
                $"  union         {methodTable.UnionKind} (canonical={methodTable.IsCanonicalMethodTable})");
            Console.WriteLine($"  parent        0x{methodTable.ParentMethodTablePointer.ToInt64():x}" +
                              $"{(type.BaseType != null ? $"  [{type.BaseType.Name}]" : "  [none]")}");

            if (eeClass != null)
            {
                Console.WriteLine($"  EEClass       0x{eeClass.Address.ToInt64():x}");
                Console.WriteLine($"    normType    {eeClass.NormType}");
                Console.WriteLine($"    fields      instance={eeClass.NumberOfInstanceFields} " +
                                  $"static={eeClass.NumberOfStaticFields} " +
                                  $"threadStatic={eeClass.NumberOfThreadStaticFields}");
                Console.WriteLine(
                    $"    methods     {eeClass.NumberOfMethods}   (EEClass.NumMethods; not the same tally as declared methods)");
            }

            PrintMethods(type, methodTable);
        }

        /// <summary>
        /// Lists each decoded MethodDesc with the name, arguments and generic type arguments
        /// recovered by resolving its reconstructed metadata token.
        /// </summary>
        private static void PrintMethods(Type type, ClrMethodTable methodTable)
        {
            Console.WriteLine($"  methods       {methodTable.Methods.Count} decoded from the MethodDescChunk list:");

            var module = type.IsConstructedGenericType
                ? type.GetGenericTypeDefinition().Module
                : type.Module;

            foreach (var method in methodTable.Methods)
            {
                MethodBase resolved;
                try
                {
                    resolved = module.ResolveMethod((int)method.MetadataToken);
                }
                catch (ArgumentException)
                {
                    Console.WriteLine(
                        $"    slot {method.SlotNumber,-5} 0x{method.MetadataToken:x8}  <token did not resolve>");
                    continue;
                }

                var generics = GenericArguments(resolved);
                var arguments = string.Join(", ", resolved.GetParameters()
                    .Select(p => $"{FriendlyName(p.ParameterType)} {p.Name}"));

                Console.WriteLine($"    slot {method.SlotNumber,-5} 0x{method.MetadataToken:x8}  " +
                                  $"{FriendlyName(resolved.DeclaringType)}.{resolved.Name}{generics}({arguments})" +
                                  $"   [{method.Classification}]");
            }
        }

        private static string GenericArguments(MethodBase method)
        {
            if (!method.IsGenericMethod && !method.IsGenericMethodDefinition)
                return string.Empty;

            return "<" + string.Join(", ", method.GetGenericArguments().Select(FriendlyName)) + ">";
        }

        private static string FriendlyName(Type type)
        {
            if (type == null)
                return "?";

            if (type.IsArray)
                return FriendlyName(type.GetElementType()) + "[]";

            if (!type.IsGenericType)
                return type.Name;

            var name = type.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0)
                name = name.Substring(0, tick);

            return name + "<" + string.Join(", ", type.GetGenericArguments().Select(FriendlyName)) + ">";
        }
    }
}