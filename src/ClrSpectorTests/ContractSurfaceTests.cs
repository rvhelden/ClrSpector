using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ClrSpector;
using ClrSpector.Detours;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace ClrSpectorTests
{
    public class Base { public int BaseField = 1; }

    public class LayoutSample : Base
    {
        public int First = 0x11111111;
        public long Second = 0x2222222222222222;
        public string Text = "t";
        public byte Small = 0x33;
        public static int StaticField = 7;
        [ThreadStatic] public static int PerThread;
    }

    public class CodeSample
    {
        [MethodImpl(MethodImplOptions.NoInlining)] public int Add(int a, int b) => a + b;
        [MethodImpl(MethodImplOptions.NoInlining)] public virtual string Virt() => "v";
        [MethodImpl(MethodImplOptions.NoInlining)] public static long Stat(long x) => x * 2;

        public string Branchy(int n)
        {
            var total = 0;
            for (var i = 0; i < n; i++) total += i % 3 == 0 ? i : -i;

            try { total += 1; }
            catch (InvalidOperationException) { total = -1; }

            return total > 0 ? "positive" : "other";
        }
    }

    /// <summary>
    /// The parts of the runtime's contract this library decodes beyond method tables: field
    /// layout, the code map, code versioning, threads, exception traces, modules and IL.
    /// </summary>
    public class ContractSurfaceTests
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
                                         | BindingFlags.Instance | BindingFlags.Static;

        // ---------- field layout ----------

        /// <summary>
        /// The runtime lays fields out in whatever order suits it, so the only honest check is
        /// against memory: write a known value, then read it back at the reported offset.
        /// </summary>
        [Test]
        public async Task FieldOffsetsPointAtTheRealFields()
        {
            var read = ReadFieldsAtTheirReportedOffsets(new LayoutSample());

            await Assert.That(read.Count).IsEqualTo(4);
            await Assert.That(read["First"]).IsEqualTo(0x11111111L);
            await Assert.That(read["Second"]).IsEqualTo(0x2222222222222222L);
            await Assert.That(read["Small"]).IsEqualTo(0x33L);

            // A reference field reads as a non-zero address rather than a value.
            await Assert.That(read["Text"]).IsNotEqualTo(0L);
        }

        /// <summary>
        /// Reads each instance field straight out of the object at the offset the runtime
        /// reported, which is the only way to prove the offset is the real one.
        /// </summary>
        private static unsafe Dictionary<string, long> ReadFieldsAtTheirReportedOffsets(LayoutSample instance)
        {
            var values = new Dictionary<string, long>();
            var methodTable = ClrObject.From(typeof(LayoutSample)).MethodTable;
            var dataStart = (byte*)ClrHeapObject.AddressOf(instance) + IntPtr.Size;

            foreach (var field in methodTable.Fields.Where(f => !f.IsStatic))
            {
                var name = typeof(LayoutSample).Module.ResolveField((int)field.MetadataToken).Name;
                var at = dataStart + field.Offset;

                values[name] = field.ElementType switch
                {
                    CorElementType.I4 => *(int*)at,
                    CorElementType.I8 => *(long*)at,
                    CorElementType.U1 => *at,
                    _ => (*(IntPtr*)at).ToInt64()
                };
            }

            return values;
        }

        /// <summary>The list holds what the type declares, not what it inherits.</summary>
        [Test]
        public async Task FieldsExcludeInheritedOnes()
        {
            var derived = ClrObject.From(typeof(LayoutSample)).MethodTable;
            var baseTable = ClrObject.From(typeof(Base)).MethodTable;

            await Assert.That(derived.DeclaredInstanceFieldCount).IsEqualTo(4);
            await Assert.That(baseTable.DeclaredInstanceFieldCount).IsEqualTo(1);
        }

        [Test]
        public async Task StaticAndThreadStaticFieldsAreFlagged()
        {
            var fields = ClrObject.From(typeof(LayoutSample)).MethodTable.Fields;

            var byName = fields.ToDictionary(
                f => typeof(LayoutSample).Module.ResolveField((int)f.MetadataToken).Name);

            await Assert.That(byName["StaticField"].IsStatic).IsTrue();
            await Assert.That(byName["PerThread"].IsThreadStatic).IsTrue();
            await Assert.That(byName["First"].IsStatic).IsFalse();
        }

        // ---------- code address to method ----------

        [Test]
        [Arguments("Add")]
        [Arguments("Virt")]
        [Arguments("Stat")]
        public async Task ResolvesACodeAddressBackToItsMethod(string name)
        {
            var method = typeof(CodeSample).GetMethod(name, All);
            RuntimeHelpers.PrepareMethod(method.MethodHandle);

            var code = MethodPrecode.Of(method).DispatchTarget;
            var block = ClrCodeMap.Current.Find(code);

            await Assert.That(block).IsNotNull();
            await Assert.That(block.Kind).IsEqualTo(ClrCodeKind.Jitted);
            await Assert.That(block.ResolveMethod()?.Name).IsEqualTo(name);
            await Assert.That(block.OffsetIntoMethod).IsEqualTo(0L);
        }

        /// <summary>An address inside a method resolves to it, which is what makes this useful.</summary>
        [Test]
        public async Task ResolvesAnAddressInsideAMethod()
        {
            var method = typeof(CodeSample).GetMethod(nameof(CodeSample.Add), All);
            RuntimeHelpers.PrepareMethod(method.MethodHandle);

            var code = MethodPrecode.Of(method).DispatchTarget;
            var block = ClrCodeMap.Current.Find(code + 4);

            await Assert.That(block.ResolveMethod()?.Name).IsEqualTo(nameof(CodeSample.Add));
            await Assert.That(block.OffsetIntoMethod).IsEqualTo(4L);
        }

        [Test]
        public async Task AnAddressThatIsNotCodeResolvesToNothing()
        {
            await Assert.That(ClrCodeMap.Current.Find(IntPtr.Zero)).IsNull();
            await Assert.That(ClrCodeMap.Current.Find(ClrHeapObject.AddressOf("a string"))).IsNull();
        }

        /// <summary>A precode is a stub, not a method body, and says so.</summary>
        [Test]
        public async Task APrecodeResolvesAsAStub()
        {
            var method = typeof(CodeSample).GetMethod(nameof(CodeSample.Add), All);
            RuntimeHelpers.PrepareMethod(method.MethodHandle);

            var block = ClrCodeMap.Current.Find(MethodPrecode.Of(method).EntryPoint);

            await Assert.That(block).IsNotNull();
            await Assert.That(block.Kind).IsEqualTo(ClrCodeKind.Stub);
        }

        // ---------- code versions ----------

        [Test]
        public async Task ReadsTheCodeVersioningState()
        {
            var method = typeof(CodeSample).GetMethod(nameof(CodeSample.Add), All);
            RuntimeHelpers.PrepareMethod(method.MethodHandle);

            var descriptor = ClrObject.From(typeof(CodeSample)).MethodTable.FindMethod(method);
            var state = descriptor.CodeVersions;

            await Assert.That(state).IsNotNull();
            await Assert.That(state.TemporaryEntryPoint).IsNotEqualTo(IntPtr.Zero);

            // Tiering is off in this project, so nothing has been recompiled.
            await Assert.That(state.HasBeenRecompiled).IsFalse();
        }

        // ---------- threads ----------

        [Test]
        public async Task ListsTheManagedThreads()
        {
            var store = ClrThreadStore.Read();

            await Assert.That(store.ThreadCount).IsGreaterThan(0);
            await Assert.That(store.Threads).IsNotEmpty();

            var mine = store.Threads.FirstOrDefault(
                t => t.ManagedThreadId == (uint)Environment.CurrentManagedThreadId);

            await Assert.That(mine).IsNotNull();
            await Assert.That(mine.OsThreadId).IsGreaterThan(0UL);
            await Assert.That(mine.StackSize).IsGreaterThan(0L);
        }

        [Test]
        public async Task SeesABackgroundThreadAsBackground()
        {
            using var gate = new ManualResetEventSlim();
            var worker = new Thread(() => gate.Wait()) { IsBackground = true };
            worker.Start();

            try
            {
                ClrThread found = null;
                for (var attempt = 0; attempt < 50 && found == null; attempt++)
                {
                    Thread.Sleep(20);
                    found = ClrThreadStore.Read().Threads
                        .FirstOrDefault(t => t.ManagedThreadId == (uint)worker.ManagedThreadId);
                }

                await Assert.That(found).IsNotNull();
                await Assert.That(found.IsBackground).IsTrue();
            }
            finally
            {
                gate.Set();
                worker.Join();
            }
        }

        // ---------- exception stack traces ----------

        [MethodImpl(MethodImplOptions.NoInlining)] private static void Deep() => throw new InvalidOperationException("boom");
        [MethodImpl(MethodImplOptions.NoInlining)] private static void Middle() => Deep();

        /// <summary>
        /// The frames come off the heap rather than out of the formatted string, so they should
        /// name the same methods the formatted string does.
        /// </summary>
        [Test]
        public async Task ReadsTheStackTraceAnExceptionCaptured()
        {
            try
            {
                Middle();
            }
            catch (Exception caught)
            {
                var frames = ClrExceptionTrace.Of(caught);

                await Assert.That(frames).IsNotEmpty();

                var names = frames.Select(f => f.ResolveMethod()?.Name).ToList();
                await Assert.That(names).Contains(nameof(Deep));
                await Assert.That(names).Contains(nameof(Middle));

                // Every frame's IP should land inside the method the frame names.
                foreach (var frame in frames.Where(f => f.ResolveMethod() != null))
                {
                    var block = ClrCodeMap.Current.Find(frame.InstructionPointer);

                    await Assert.That(block?.ResolveMethod()?.Name)
                        .IsEqualTo(frame.ResolveMethod().Name);
                }

                await Assert.That(ClrExceptionTrace.Dump(caught)).Contains(nameof(Deep));
            }
        }

        [Test]
        public async Task AnExceptionNeverThrownHasNoFrames()
        {
            await Assert.That(ClrExceptionTrace.Of(new Exception("unthrown"))).IsEmpty();
        }

        // ---------- modules ----------

        [Test]
        [Arguments(typeof(LayoutSample))]
        [Arguments(typeof(string))]
        [Arguments(typeof(int))]
        public async Task ResolvesATypeDefTokenToItsMethodTable(Type type)
        {
            var module = ClrModule.Of(type);
            var methodTable = ClrObject.From(type).MethodTable;

            await Assert.That(module.TypeDefToMethodTable(methodTable.TypeDefToken))
                .IsEqualTo(methodTable.Address);
        }

        [Test]
        public async Task ResolvesAMethodDefTokenToItsMethodDesc()
        {
            var method = typeof(object).GetMethod(nameof(ToString));
            var expected = ClrObject.From(typeof(object)).MethodTable.FindMethod(method);

            var found = ClrModule.Of(typeof(object)).MethodDefToMethodDesc((uint)method.MetadataToken);

            await Assert.That(found).IsEqualTo(expected.ClrPointer);
        }

        [Test]
        public async Task ReadsModuleAndAssemblyIdentity()
        {
            var module = ClrModule.Of(typeof(object));

            await Assert.That(module.SimpleName).IsEqualTo("System.Private.CoreLib");
            await Assert.That(module.Path).Contains("System.Private.CoreLib");

            var assembly = ClrAssembly.Of(typeof(object));

            await Assert.That(assembly.IsDynamic).IsFalse();
            await Assert.That(assembly.IsCollectible).IsFalse();
        }

        [Test]
        public async Task ReadsTheLoaderAllocatorsHeaps()
        {
            var allocator = ClrLoaderAllocator.Of(typeof(object));

            await Assert.That(allocator).IsNotNull();
            await Assert.That(allocator.OwnsPrecode).IsTrue();
            await Assert.That(allocator.HighFrequencyHeap).IsNotEqualTo(IntPtr.Zero);
        }

        // ---------- IL ----------

        /// <summary>
        /// Every instruction's length must add up to exactly the body's size - a decoder that
        /// mis-sizes one operand desynchronises and would still "work" without this.
        /// </summary>
        [Test]
        public async Task DecodedInstructionsCoverTheBodyExactly()
        {
            var il = ClrMethodIl.Of(typeof(CodeSample).GetMethod(nameof(CodeSample.Branchy), All));

            await Assert.That(il).IsNotNull();
            await Assert.That(il.Instructions.Sum(i => i.Length)).IsEqualTo(il.Bytes.Length);
            await Assert.That(il.Instructions[il.Instructions.Count - 1].NextOffset)
                .IsEqualTo(il.Bytes.Length);
        }

        [Test]
        public async Task BranchTargetsLandOnInstructionBoundaries()
        {
            var il = ClrMethodIl.Of(typeof(CodeSample).GetMethod(nameof(CodeSample.Branchy), All));

            var offsets = il.Instructions.Select(i => i.Offset).ToHashSet();

            foreach (var instruction in il.Instructions)
            {
                if (instruction.Operand is ClrIlBranchTarget branch)
                    await Assert.That(offsets).Contains(branch.Target);

                if (instruction.Operand is int[] targets)
                    foreach (var target in targets)
                        await Assert.That(offsets).Contains(target);
            }
        }

        [Test]
        public async Task ResolvesOperandsAndRendersADump()
        {
            var il = ClrMethodIl.Of(typeof(CodeSample).GetMethod(nameof(CodeSample.Branchy), All));

            var dump = il.Dump();

            await Assert.That(dump).Contains("IL_0000");
            await Assert.That(dump).Contains("ret");
            await Assert.That(dump).Contains("\"positive\"");

            // The try/catch in the method should be reported.
            await Assert.That(il.ExceptionHandlers).IsNotEmpty();
        }

        [Test]
        public async Task AMethodWithNoBodyDecodesToNull()
        {
            var abstractMethod = typeof(ReportBase).GetMethod(nameof(ReportBase.Render), All);

            await Assert.That(ClrMethodIl.Of(abstractMethod)).IsNull();
        }

        // ---------- async continuations ----------

        /// <summary>
        /// The layout check that stands behind the decoder: the contract's offsets against the
        /// managed type's own field layout, read through <see cref="ClrMethodTable.Fields"/>.
        /// <see cref="ContinuationTests"/> covers the decode of a live suspended chain.
        /// </summary>
        [Test]
        public async Task ContinuationLayoutMatchesTheManagedType()
        {
            var type = typeof(object).Assembly.GetType("System.Runtime.CompilerServices.Continuation");

            if (type == null)
                return;

            var contract = ClrObject.Descriptor.GetDataType("ContinuationObject");
            var fields = ClrObject.From(type).MethodTable.Fields;

            foreach (var name in new[] { "Next", "ResumeInfo", "State" })
            {
                if (!contract.HasField(name))
                    continue;

                var field = fields.FirstOrDefault(
                    f => type.Module.ResolveField((int)f.MetadataToken).Name == name);

                await Assert.That(field).IsNotNull();

                // Contract offsets are from the object header; FieldDesc offsets from its data.
                await Assert.That((int)field.Offset).IsEqualTo(contract[name] - IntPtr.Size);
            }
        }
    }
}
