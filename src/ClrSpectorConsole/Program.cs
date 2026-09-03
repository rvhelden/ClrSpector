using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ClrSpector;
using ClrSpector.Cdac;
using ClrSpector.Detours;

namespace ClrSpectorConsole
{
    internal static unsafe class Program
    {
        private static void Main()
        {
            Section("runtime and contracts", Runtime);
            Section("type layout", TypeLayout);
            Section("field layout", Fields);
            Section("methods", Methods);
            Section("interfaces", Interfaces);
            Section("names and IL straight from memory", FromMemory);
            Section("IL disassembly", Disassembly);
            Section("one method as IL, as C#, and as structured C#", CSharpProjection);
            Section("dispatch: precode and vtable", Dispatch);
            Section("an address back to its method", CodeMap);
            Section("tiering", Tiering);
            Section("detour: a proxy object", ProxyDetour);
            Section("detour: a new method body", ReplaceBody);
            Section("async continuations", Continuations);
            Section("threads", Threads);
            Section("an exception's captured frames", ExceptionFrames);
            Section("modules, assemblies, loader heaps", Modules);
            Section("assembly metadata: tables, heaps, entries", MetadataTables);
            Section("signatures without reflection", Signatures);
            Section("generics: what metadata cannot tell you", Generics);
            Section("attributes without constructing them", Attributes);
            Section("one object on the heap", HeapObject);
            Section("the GC heap", Heap);
        }

        // ----------------------------------------------------------------------------------

        private static void Runtime()
        {
            var runtime = ContractDescriptor.Current;

            Line("runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            Line("architecture", runtime.Globals.Text("Architecture"));
            Line("descriptor", $"v{runtime.Version}, {runtime.TypeNames.Count()} types, " +
                               $"{runtime.Contracts.Count} contracts");
            Line("gc", GcContractDescriptor.Identifiers);
        }

        private static void TypeLayout()
        {
            var table = ClrObject.From<Order>().MethodTable;

            Line("MethodTable", table.ToString());
            Line("token", $"0x{table.TypeDefToken:x8} -> {table.MetadataName}");
            Line("flags", $"class={table.IsClass} gcPointers={table.ContainsGcPointers} " +
                          $"generic={table.HasInstantiation} virtuals={table.NumberOfVirtuals}");
            Line("parent", table.ParentMethodTable?.Name);
        }

        private static void Fields()
        {
            var table = ClrObject.From<Order>().MethodTable;
            var order = new Order();
            var data = (byte*)ClrHeapObject.AddressOf(order) + IntPtr.Size;

            // The runtime lays fields out in whatever order suits it, so the offsets are read
            // back out of a live object to show they are the real ones.
            foreach (var field in table.Fields)
            {
                var name = table.Metadata.FieldName(field.MetadataToken);
                var value = field.IsStatic ? "(static)" : ValueAt(data + field.Offset, field.ElementType);

                Line($"+{field.Offset,-3} {name}", $"{field.ElementType} = {value}");
            }
        }

        private static void Methods()
        {
            var table = ClrObject.From<Order>().MethodTable;

            foreach (var method in table.Methods.Take(4))
                Line($"slot {method.SlotNumber}", method.ToString());
        }

        private static void Interfaces()
        {
            var table = ClrObject.From<Order>().MethodTable;

            // Metadata lists what the type declares; the MethodTable's count is the runtime's
            // closure over everything inherited too, and the contract publishes no map for it.
            Line("declared", $"{table.DeclaredInterfaces.Count} of the runtime's " +
                             $"{table.NumberOfInterfaces} implemented");

            foreach (var implemented in table.DeclaredInterfaces)
            {
                Line("  interface", implemented.ToString());

                var iface = implemented.Interface;
                if (iface == null)
                    continue;

                foreach (var method in iface.Methods)
                {
                    // An interface method with a body is a default implementation.
                    Line($"    {method.Name}", method.HasBody ? "default implementation" : "abstract");

                    if (!method.HasBody)
                        continue;

                    foreach (var line in ClrMethodIl.Of(method).Dump()
                                 .Split(Environment.NewLine).Skip(2).Take(4))
                        Console.WriteLine($"        {line.TrimEnd()}");
                }

                foreach (var field in iface.Fields)
                    Line($"    field {iface.Metadata.FieldName(field.MetadataToken)}",
                        $"static={field.IsStatic} {field.ElementType}");
            }
        }

        private static void FromMemory()
        {
            var table = ClrObject.From<Order>().MethodTable;
            var describe = table.FindMethod("Describe");

            // Nothing here creates a Type or a MethodBase: the names come from the module's
            // string heap and the IL from the mapped image.
            Line("type", table.MetadataName);
            Line("method", $"{describe.DeclaringTypeName}::{describe.Name}");
            Line("body", describe.ReadIl().ToString());
        }

        private static void Disassembly()
        {
            var describe = ClrObject.From<Order>().MethodTable.FindMethod("Describe");
            var il = ClrMethodIl.Of(describe);

            // Auto colours only when the output looks like a terminal that wants it.
            foreach (var line in il.Dump(IlDumpStyle.Auto).Split('\n').Take(8))
                Console.WriteLine($"  {line.TrimEnd()}");
        }

        /// <summary>
        /// One method rendered three ways, so the difference between them is visible rather than
        /// described: the IL as it is, the same IL as C# with nothing inferred, and the same IL
        /// again with the compiler's scaffolding undone.
        /// </summary>
        private static void CSharpProjection()
        {
            // Read through the MethodDesc rather than reflection, to show that none of this
            // needs anything reflection knows: the locals are typed from the body's own local
            // signature, the try and catch blocks come out of its data sections, and the caught
            // type and every operand are named from the module's metadata.
            var fromMemory = ClrMethodIl.Of(
                ClrObject.From(typeof(AwaitChain)).MethodTable.FindMethod(nameof(AwaitChain.AwaitsTheInnerCall)));

            var faithful = fromMemory.ToCSharp();
            var structured = fromMemory.ToCSharp(ClrCSharpForm.Structured);

            Line("method", $"{fromMemory.Description.DeclaringTypeName}::{fromMemory.Description.Name}");
            Line("its source", "ClrSpectorConsole/Order.cs, to read the three views against");
            Line("body in memory", fromMemory.Description.ReadIl().ToString());

            // The locals below are named only because a PDB was found to read them from; the
            // metadata and the runtime have their types and nothing else.
            var symbols = ClrModuleSymbols.AtImageBase(fromMemory.Description.Metadata.ImageBase);

            Line("symbols", symbols?.ToString() ?? "none found - locals keep their slot numbers");

            foreach (var local in fromMemory.LocalVariables)
                Line("  local", local.ToString());

            foreach (var region in fromMemory.ExceptionRegions)
                Line("  region", region.ToString());

            Line("sizes", $"{fromMemory.Instructions.Count} instructions -> " +
                          $"{faithful.Lines.Count} faithful lines -> {structured.Lines.Count} structured");

            // All three take the same IlDumpStyle, so Auto colours in a terminal and not in a
            // pipe, and one palette covers the lot.
            View("the IL", fromMemory.Dump(IlDumpStyle.Auto));
            View("as C#, faithful: the stack undone, the control flow as it is", faithful.Dump(IlDumpStyle.Auto));
            View("as C#, structured: the compiler's scaffolding undone too", structured.Dump(IlDumpStyle.Auto));
        }

        /// <summary>One rendering of a method, under a heading of its own.</summary>
        private static void View(string title, string text)
        {
            Console.WriteLine();
            Console.WriteLine($"  -- {title} " + new string('-', Math.Max(0, 71 - title.Length)));

            foreach (var line in text.Split(Environment.NewLine))
                Console.WriteLine($"  {line.TrimEnd()}");
        }

        private static void Dispatch()
        {
            var table = ClrObject.From<Order>().MethodTable;
            var describe = table.FindMethod("Describe");
            var ship = table.FindMethod("Ship");

            var precode = MethodPrecode.Of(describe);

            Line("precode", precode.ToString());
            Line("kind", $"fixup={precode.IsFixupPrecode} (matched against the runtime's own template)");

            // Only a virtual method has a vtable slot; a non-virtual one dispatches through a
            // slot packed in after its own MethodDesc instead. Both come from the MethodDesc's
            // own slot number, so nothing has to match metadata tokens to find them.
            Line("vtable slot", $"Describe={Slot(MethodVtable.FindSlot(describe))} (not virtual)  " +
                                $"Ship={Slot(MethodVtable.FindSlot(ship))}");
            Line("non-vtable slot", $"{Slot(describe.NonVtableSlotAddress)} holds its entry point");
        }

        private static void CodeMap()
        {
            var total = ClrObject.From<Order>().MethodTable.FindMethod("Total");

            // The MethodDesc address is a RuntimeMethodHandle, so it can be jitted without a
            // MethodInfo anywhere in sight.
            total.Prepare();

            var precode = MethodPrecode.Of(total);
            var code = precode.DispatchTarget;

            Line("entry point", ClrCodeMap.Current.Find(precode.EntryPoint)?.ToString());
            Line("its code", ClrCodeMap.Current.Find(code)?.ToString());
            Line("four bytes in", ClrCodeMap.Current.Find(code + 4)?.ToString());
            Line("not code", ClrCodeMap.Current.Find(ClrHeapObject.AddressOf("a string"))?.ToString() ?? "(nothing)");
        }

        private static void Tiering()
        {
            var total = ClrObject.From<Order>().MethodTable.FindMethod("Total");

            Line("eligible", total.IsEligibleForTieredCompilation.ToString());
            Line("versions", total.CodeVersions?.ToString());
            Line("why it matters", "a redirect is refused on an eligible method - promotion " +
                                   "rewrites the same slot the detour patches");
        }

        private static void ProxyDetour()
        {
            var order = new Order();
            var proxy = new OrderProxy();

            Line("before", order.Describe(9));

            var target = ClrObject.From<Order>().MethodTable.FindMethod("Describe");
            var standIn = ClrObject.From<OrderProxy>().MethodTable.FindMethod("Describe");

            using (var detour = MethodDetour.Redirect(target, proxy, standIn))
            {
                Line("redirected", $"{order.Describe(9)}  (pairing={detour.Pairing}, thunk={detour.UsesThunk})");
            }

            Line("proxy saw", string.Join(", ", proxy.Seen));
            Line("restored", order.Describe(9));
        }

        private static void ReplaceBody()
        {
            var order = new Order();
            var total = ClrObject.From<Order>().MethodTable.FindMethod("Total");

            Line("before", order.Total().ToString());

            // decimal is returned through a hidden buffer, so the replacement has to be emitted
            // as an instance method or the return value lands in the object instead.
            using (MethodDetour.ReplaceBody(total, il =>
                   {
                       il.Emit(OpCodes.Ldc_I4, 99);
                       il.Emit(OpCodes.Newobj, typeof(decimal).GetConstructor(new[] { typeof(int) }));
                       il.Emit(OpCodes.Ret);
                   }))
            {
                Line("replaced", $"{order.Total()}  (Quantity still {order.Quantity})");
            }

            Line("restored", order.Total().ToString());
        }

        private static void Continuations()
        {
            // .NET 11 runtime async replaces the compiler's state machine with a heap object per
            // suspension, so a suspended await chain becomes a linked list this can walk.
            var contract = GcContractDescriptor.Current.TryGetDataType("ContinuationObject", out _)
                           || ContractDescriptor.Current.TryGetDataType("ContinuationObject", out _);

            Line("contract", contract ? "ContinuationObject is published" : "not published");

            var type = typeof(object).Assembly.GetType("System.Runtime.CompilerServices.Continuation");
            Line("managed type", type == null ? "absent" : type.FullName);

            // Two awaits deep and parked: while the gate's task is pending, the continuations
            // that will resume these methods are still on the heap and still linked together.
            var pending = AwaitChain.AwaitsTheInnerCall();

            try
            {
                // A suspended method parks its continuation in the awaited task's own
                // continuation slot, which reaches the chain without walking the heap.
                var slot = typeof(Task).GetField(
                    "m_continuationObject",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                var head = slot?.GetValue(AwaitChain.Gate.Task);

                Line("live instance", head == null ? "nothing suspended" : head.GetType().FullName);

                if (head != null)
                {
                    var continuation = ClrContinuation.Of(head);

                    Line("chain", $"{continuation.Chain().Count} links");
                    Console.WriteLine(continuation.Dump());
                }
            }
            finally
            {
                AwaitChain.Gate.TrySetResult(40);
            }

            Line("resumed", $"result {pending.GetAwaiter().GetResult()}");
        }

        private static void Threads()
        {
            using var gate = new ManualResetEventSlim();
            var worker = new Thread(() => gate.Wait()) { IsBackground = true };
            worker.Start();
            Thread.Sleep(50);

            var store = ClrThreadStore.Read();
            Line("store", store.ToString());

            foreach (var thread in store.Threads.Take(3))
                Line($"  thread {thread.ManagedThreadId}", thread.ToString());

            gate.Set();
            worker.Join();
        }

        private static void ExceptionFrames()
        {
            try
            {
                Throwing();
            }
            catch (Exception caught)
            {
                // The frames are read off the exception object, not parsed out of its string.
                foreach (var frame in ClrExceptionTrace.Of(caught).Take(3))
                    Line("frame", ClrCodeMap.Current.Find(frame.InstructionPointer)?.ToString() ?? frame.ToString());
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Throwing() => throw new InvalidOperationException("sample");

        private static void Modules()
        {
            // The MethodTable records its module, so everything else follows from there.
            var table = ClrObject.From<Order>().MethodTable;
            var module = ClrModule.At(table.Module);
            var assembly = ClrAssembly.At(module.Assembly);

            Line("module", module.ToString());
            Line("assembly", assembly.ToString());
            Line("loader heaps", ClrLoaderAllocator.At(module.LoaderAllocator).ToString());

            // A token reaches a MethodTable with no Type involved.
            var token = table.TypeDefToken;
            Line("token lookup", $"0x{token:x8} -> 0x{module.TypeDefToMethodTable(token).ToInt64():x}");

            // The runtime's Assembly stores no name - only a Module. The name is a row in that
            // module's metadata, read here by the same route as every other name: a token, not
            // reflection. Every one of these strings came out of the mapped image.
            Line(string.Empty, string.Empty);

            foreach (var subject in new[] { typeof(Order), typeof(object), typeof(Uri), typeof(Enumerable) })
            {
                var each = ClrAssembly.Of(subject);
                var culture = each.Culture ?? "neutral";

                Line("assembly", $"{each.Name} {each.Version} ({culture})" +
                                 $"{(each.IsCollectible ? " collectible" : string.Empty)}" +
                                 $"{(each.IsDynamic ? " dynamic" : string.Empty)}");

                foreach (var eachModule in each.Modules)
                {
                    Line("  module", $"{eachModule.SimpleName}  " +
                                     $"{ClrModuleMetadata.Of(eachModule).Image}");
                }
            }
        }

        private static void MetadataTables()
        {
            // Two images of very different size, because the size is what makes this interesting:
            // ECMA-335 column widths are not fixed. A heap index is two bytes until the heap
            // passes 64 KB, a table index until the table passes 65535 rows - so the same schema
            // measures to different row sizes in these two, and every row offset shifts with it.
            foreach (var subject in new[] { typeof(Order), typeof(object) })
            {
                var module = ClrModule.Of(subject);
                var image = ClrModuleMetadata.Of(module).Image;

                Console.WriteLine();
                Line("module", $"{module.SimpleName}  metadata {image.MetadataSizeOf()} bytes, " +
                               $"version {image.Version}");

                Line("heaps", $"strings {image.StringHeapSize} bytes / " +
                              $"{image.StringIndexSize}-byte indexes, " +
                              $"blobs {image.BlobHeapSize} / {image.BlobIndexSize}-byte, " +
                              $"user strings {image.UserStringHeapSize}");

                // Only the tables this module actually uses; most of the 45 are empty.
                var populated = Enum.GetValues<MetadataTable>()
                    .Where(t => image.RowCount(t) > 0)
                    .OrderByDescending(t => image.RowCount(t))
                    .ToList();

                Line("tables", $"{populated.Count} of 45 populated");

                foreach (var table in populated.Take(10))
                {
                    Line($"  {table}", $"{image.RowCount(table),7} rows" +
                                       $"{(image.IsSorted(table) ? "  sorted" : string.Empty)}");
                }

                // And an actual entry out of two of them, to show the rows really are readable.
                var firstType = ClrModuleMetadata.Of(module)
                    .FullTypeName(((uint)MetadataTable.TypeDef << 24) | 2);

                var lastMethodRow = (uint)image.RowCount(MetadataTable.MethodDef);
                var lastMethod = ClrModuleMetadata.Of(module)
                    .MethodName(((uint)MetadataTable.MethodDef << 24) | lastMethodRow);

                Line("  TypeDef row 2", firstType);
                Line($"  MethodDef row {lastMethodRow}", lastMethod);
            }
        }

        /// <summary>
        /// A method's parameters and return type, decoded from its signature blob.
        /// </summary>
        /// <remarks>
        /// Nothing here creates a MethodInfo, and nothing goes through
        /// System.Reflection.Metadata either - the blob is parsed out of the mapped image with
        /// the CorElementType enum. The interesting cases are the ones reflection either cannot
        /// reach or reports less precisely.
        /// </remarks>
        private static void Signatures()
        {
            var table = ClrObject.From<Order>().MethodTable;

            foreach (var method in table.Methods.Take(6))
                Line(method.Name, method.Signature?.ToString());

            // out and in are Param-table attributes, not part of the blob - which only says
            // BYREF - so a decoder reading the signature alone reports every one as "ref".
            var direction = ClrObject.From<Dictionary<string, int>>().MethodTable
                .FindMethod("TryGetValue");

            if (direction != null)
                Line("out parameter", direction.Signature?.ToString());

            // An array's Get, Set and Address are synthesised per array type and declared
            // nowhere, so they have no metadata row at all - their signature is on the
            // MethodDesc itself.
            Line(string.Empty, string.Empty);
            foreach (var method in ClrObject.From<int[]>().MethodTable.Methods)
            {
                Line($"int[] accessor", $"{method.Signature}   " +
                                        $"(stored on the MethodDesc: {method.Signature?.IsStored})");
            }

            // Same for an emitted method: no row, no string heap entry, so both its name and its
            // signature come off the MethodDesc.
            Line(string.Empty, string.Empty);
            var emitted = new DynamicMethod("Emitted", typeof(int), new[] { typeof(int), typeof(string) });
            var body = emitted.GetILGenerator();
            body.Emit(OpCodes.Ldarg_0);
            body.Emit(OpCodes.Ret);
            emitted.CreateDelegate(typeof(Func<int, string, int>));

            var accessor = typeof(DynamicMethod).GetMethod(
                "GetMethodDescriptor", BindingFlags.Instance | BindingFlags.NonPublic);
            var handle = (RuntimeMethodHandle)accessor.Invoke(emitted, null);
            var dynamic = ClrMethodDescription.At(handle.Value);

            Line("dynamic method", $"{dynamic.Name} : {dynamic.Signature}");
            Line("its name source", "DynamicMethodDesc.MethodName - there is no string heap entry");
        }

        /// <summary>
        /// The generic instantiations a process has actually made, which metadata does not record.
        /// </summary>
        /// <remarks>
        /// Metadata holds only open definitions: there is no MethodDef row for
        /// <c>Echo&lt;int&gt;</c> and no TypeDef row for <c>List&lt;int&gt;</c>. The runtime
        /// builds those on demand and files them in per-module hash tables, so those tables are
        /// the only place the real instantiations can be seen.
        /// </remarks>
        private static void Generics()
        {
            // A type's own instantiation, from its MethodTable rather than from metadata.
            var closed = ClrObject.From<Dictionary<string, int>>().MethodTable;
            var arguments = closed.TypeArguments.Select(NameOfHandle);

            Line("Dictionary<string,int>", $"{closed.GenericTypeArgumentCount} type arguments: " +
                                           $"{string.Join(", ", arguments)}");

            var indexer = closed.Methods.FirstOrDefault(m => m.Name == "get_Item");
            if (indexer != null)
            {
                // The blob says !1 (!0); closing it needs the instantiation.
                Line("  get_Item open", indexer.Signature?.ToString());
                Line("  closed exactly", indexer.ClosedSignatureFor(closed)?.ToString());
                Line("  closed as compiled", indexer.ClosedSignature?.ToString());
            }

            // Force a spread of instantiations, then read them back out of the runtime.
            Echo(1);
            Echo(2.5);
            Echo("text");
            Echo((object)null);

            var module = ClrModule.Of(typeof(Program));
            var before = module.InstantiatedMethodCount;
            var entries = module.InstantiatedMethodEntries.ToList();

            Line(string.Empty, string.Empty);
            Line("instantiated methods", $"{entries.Count} walked, table reports {before} " +
                                         $"(a walk of the runtime's own side table)");

            foreach (var (method, flags) in entries.Where(e => e.Method.Name == "Echo"))
            {
                var argument = NameOfHandle(method.GenericArgumentMethodTables.FirstOrDefault());
                var note = flags == InstantiatedMethodFlags.None ? string.Empty : $"  [{flags}]";

                Line($"  Echo<{argument}>", $"{method.GenericKind}{note}");
                Line("     closed", method.ClosedSignature?.ToString());
            }

            Line(string.Empty, string.Empty);
            Line("why three kinds", "a value type argument gets its own code; every reference " +
                                    "argument shares one body compiled against __Canon, with a " +
                                    "stub per concrete instantiation over it");

            var coreLib = ClrModule.Of(typeof(object));
            Line("CoreLib side tables", $"{coreLib.InstantiatedMethodCount} instantiated methods, " +
                                        $"{coreLib.ConstructedTypeCount} constructed types");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static T Echo<T>(T value) => value;

        private static string NameOfHandle(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                return "?";

            // A type variable or a pointer type is a TypeDesc, not a MethodTable.
            return ClrMethodTable.IsMethodTableHandle(handle)
                ? ClrMethodTable.Create(new MemoryReader(handle)).MetadataName
                : "<type variable>";
        }

        /// <summary>
        /// The custom attributes applied to a type, a field, a method and the assembly, with the
        /// values the source wrote.
        /// </summary>
        /// <remarks>
        /// Reflection can do this, but not without cost: <c>GetCustomAttributes</c> constructs
        /// each attribute, which runs its constructor in this process, and needs the attribute's
        /// type to load. Reading the CustomAttribute row instead runs nothing - what comes back
        /// is the blob the compiler wrote, decoded.
        /// </remarks>
        private static void Attributes()
        {
            var table = ClrObject.From<Order>().MethodTable;

            foreach (var attribute in table.CustomAttributes)
                Line("on the type", attribute.ToString());

            // Each argument knows more than the rendering shows: where it came from, how it is
            // stored, and - for an enum - what its underlying type turned out to be.
            var detailed = table.CustomAttributes
                .OrderByDescending(a => a.ConstructorArguments.Count + a.NamedArguments.Count)
                .First();

            Line(string.Empty, string.Empty);
            Line("its constructor", detailed.Constructor?.ToString());
            Line("its blob", $"{detailed.ValueLength} bytes at " +
                             $"0x{detailed.ValueAddress.ToInt64():x}");

            foreach (var argument in detailed.ConstructorArguments)
                Line($"  arg {argument.Position} {argument.ParameterName}", Describe(argument));

            foreach (var argument in detailed.NamedArguments)
                Line($"  {argument.Kind.ToString().ToLowerInvariant()} {argument.Name}", Describe(argument));

            Line(string.Empty, string.Empty);

            var field = table.Fields.FirstOrDefault(f => f.Name == "Quantity");
            foreach (var attribute in field?.CustomAttributes ?? new ClrCustomAttribute[0])
                Line("on a field", attribute.ToString());

            var method = table.FindMethod("Describe");
            foreach (var attribute in method?.CustomAttributes ?? new ClrCustomAttribute[0])
                Line("on a method", attribute.ToString());

            // The one systematic gap, said out loud rather than papered over: ECMA-335 II.21
            // attributes are compiled into bits in the defining table, not into rows, so there is
            // nothing in the CustomAttribute table to find. Reflection synthesises them back.
            Line(string.Empty, string.Empty);
            Line("not in the table", "[MethodImpl] is on Describe in source but is a bit in " +
                                     "MethodDef.ImplFlags, so no row carries it");

            Line(string.Empty, string.Empty);
            foreach (var attribute in ClrAssembly.Of(typeof(object)).CustomAttributes.Take(4))
                Line("[assembly:] in CoreLib", attribute.ToString());

            var metadata = ClrModuleMetadata.Of(ClrModule.Of(typeof(object)));
            var total = 0;
            var failed = 0;

            foreach (var attribute in metadata.AllCustomAttributes)
            {
                total++;

                if (!attribute.IsDecoded)
                    failed++;
            }

            Line(string.Empty, string.Empty);
            Line("CoreLib as a whole", $"{total} attribute rows, {failed} that would not decode");
        }

        /// <summary>One argument with everything known about it, not just its value.</summary>
        private static string Describe(ClrAttributeArgument argument)
        {
            var stored = argument.Type.IsEnum
                ? $"{argument.Type.TypeName} stored as {argument.Type.Underlying}" +
                  (argument.Type.UnderlyingResolved ? string.Empty : " (assumed)")
                : argument.Type.ToString();

            return $"{argument.Literal(),-40} {stored}";
        }

        private static void HeapObject()
        {
            using var scope = GcWalkScope.Enter();
            var heap = ClrGcHeap.Refresh();

            foreach (var instance in new object[] { new Order(), "text", new byte[100_000] })
            {
                var entry = ClrHeapObject.Of(instance, scope);

                Line(entry.MethodTable.Name?.Split('.').Last(),
                    $"{entry.Size,7} bytes  gen {entry.GenerationIn(heap)}  " +
                    $"loh={entry.SegmentIn(heap)?.IsLargeObjectHeap}");
            }
        }

        private static void Heap()
        {
            using var scope = GcWalkScope.Enter();
            var heap = ClrGcHeap.Refresh();

            Line("heap", heap.ToString());
            Line("protected", $"collection held off = {scope.IsProtected}");

            var walked = 0;
            long bytes = 0;
            var free = 0;

            foreach (var instance in heap.EnumerateObjects(scope))
            {
                walked++;
                bytes += instance.Size;
                if (instance.IsFree) free++;
            }

            Line("walked", $"{walked} objects, {bytes} bytes, {free} free fillers");
            Line("trustworthy", $"collection during walk = {scope.CollectionOccurred}");
        }

        // ----------------------------------------------------------------------------------

        private static string ValueAt(byte* at, CorElementType type)
        {
            switch (type)
            {
                case CorElementType.I4:
                    return (*(int*)at).ToString();

                case CorElementType.CLASS:
                    return $"ref 0x{(*(IntPtr*)at).ToInt64():x}";

                default:
                    // A struct field: show its first bytes rather than guess at a format.
                    return $"raw 0x{*(long*)at:x16}";
            }
        }

        private static void Section(string title, Action body)
        {
            Console.WriteLine();
            Console.WriteLine($"--- {title} " + new string('-', Math.Max(0, 74 - title.Length)));

            try
            {
                body();
            }
            catch (Exception error)
            {
                // A section that cannot run says why and the rest still does - this is a sample,
                // not a test, and one unsupported feature should not hide the others.
                Console.WriteLine($"  ! {error.GetType().Name}: {error.Message.Split('\n')[0]}");
            }
        }

        private static string Slot(IntPtr address)
        {
            return address == IntPtr.Zero ? "(none)" : $"0x{address.ToInt64():x}";
        }

        private static void Line(string label, string value)
        {
            Console.WriteLine($"  {label,-22} {value}");
        }
    }
}