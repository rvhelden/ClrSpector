using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using ClrSpector;
using ClrSpector.Cdac;
using ClrSpector.Detours;

namespace ClrSpectorConsole
{
    /// <summary>A small type to point everything at.</summary>
    public class Order
    {
        public int Quantity = 3;

        public decimal UnitPrice = 2.5m;

        public string Sku = "A-1";

        [MethodImpl(MethodImplOptions.NoInlining)]
        public decimal Total() => this.Quantity * this.UnitPrice;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public string Describe(int wanted) => wanted > this.Quantity ? "short" : "ok";

        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual string Ship() => "shipped";
    }

    /// <summary>A stand-in with state of its own, for the proxy detour.</summary>
    public class OrderProxy
    {
        public readonly List<string> Seen = new List<string>();

        [MethodImpl(MethodImplOptions.NoInlining)]
        public string Describe(Order order, int wanted)
        {
            this.Seen.Add($"{order.Sku}/{wanted}");

            return "proxied";
        }
    }

    /// <summary>
    /// One short demonstration of each thing this library can do. Every section is deliberately
    /// small - the point is to show the entry point and one line of real output, not to be a
    /// tool.
    /// </summary>
    internal static unsafe class Program
    {
        private static void Main()
        {
            Section("runtime and contracts", Runtime);
            Section("type layout", TypeLayout);
            Section("field layout", Fields);
            Section("methods", Methods);
            Section("names and IL straight from memory", FromMemory);
            Section("IL disassembly", Disassembly);
            Section("dispatch: precode and vtable", Dispatch);
            Section("an address back to its method", CodeMap);
            Section("tiering", Tiering);
            Section("detour: a proxy object", ProxyDetour);
            Section("detour: a new method body", ReplaceBody);
            Section("async continuations", Continuations);
            Section("threads", Threads);
            Section("an exception's captured frames", ExceptionFrames);
            Section("modules, assemblies, loader heaps", Modules);
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

            // Reaching one needs a method the runtime actually compiled as async; on this preview
            // the compiler still emits state machines, so there is nothing to decode.
            Line("live instance", "none on this runtime - ClrContinuation.Of(obj) decodes one " +
                                  "when a Continuation exists");
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

            Line("module", module.ToString());
            Line("assembly", ClrAssembly.At(module.Assembly).ToString());
            Line("loader heaps", ClrLoaderAllocator.At(module.LoaderAllocator).ToString());

            // A token reaches a MethodTable with no Type involved.
            var token = table.TypeDefToken;
            Line("token lookup", $"0x{token:x8} -> 0x{module.TypeDefToMethodTable(token).ToInt64():x}");
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