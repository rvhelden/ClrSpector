# ClrSpector

Reads CoreCLR's private in-memory data structures from inside the running process, and uses that
to **detour a method call** — swapping a concrete method for a stand-in so it can be mocked
without the production type needing an interface. Static, non-virtual and virtual methods are all
supported, and the precode and vtable machinery it relies on is exposed for inspection.

Targets **.NET 10**. Verified on .NET 10.0.4 / linux-x64.

```csharp
// Inspect what the runtime knows about a type
var methodTable = ClrObject.From<Order>().MethodTable;
foreach (var method in methodTable.Methods)
    Console.WriteLine(method.MetadataToken);

// Stand in for a concrete method for the duration of a test - no interface required.
// Works for static, non-virtual and virtual methods alike.
using (MethodDetour.Redirect(
           typeof(PriceService), nameof(PriceService.GetPrice),
           typeof(PriceServiceProxy), nameof(PriceServiceProxy.GetPrice)))
{
    Assert.Equal(42m, new PriceService().GetPrice("abc"));  // the proxy answers
}
// original behaviour restored here

// Or just look at how a method is dispatched
Console.WriteLine(MethodPrecode.Of(typeof(PriceService).GetMethod("GetPrice")));
// PriceService.GetPrice entryPoint=0x… [ff 25 fa 3f 00 00 …] jmp qword [rip+16378]
//                       slot=0x… -> 0x…
```

---

## Table of contents

- [Why this is hard](#why-this-is-hard)
- [How the inspector works](#how-the-inspector-works)
  - [The contract descriptor](#the-contract-descriptor)
  - [Reading a type](#reading-a-type)
  - [Enumerating methods](#enumerating-methods)
  - [Recovering names and signatures](#recovering-names-and-signatures)
  - [Two traps](#two-traps)
  - [Failing loudly](#failing-loudly)
- [How method detouring works](#how-method-detouring-works)
  - [The problem](#the-problem)
  - [What can be detoured](#what-can-be-detoured)
  - [What does not work: patching the code](#what-does-not-work-patching-the-code)
  - [What a precode is](#what-a-precode-is)
  - [Finding the dispatch slot](#finding-the-dispatch-slot)
  - [Virtual calls go through the vtable instead](#virtual-calls-go-through-the-vtable-instead)
  - [Where the vtable lives, and why it is chunked](#where-the-vtable-lives-and-why-it-is-chunked)
  - [Performing the swap](#performing-the-swap)
  - [Why it catches every call shape](#why-it-catches-every-call-shape)
  - [Interface dispatch cannot be undone](#interface-dispatch-cannot-be-undone)
  - [Keeping it safe](#keeping-it-safe)
  - [Limits you must know](#limits-you-must-know)
  - [Writing a proxy](#writing-a-proxy)
  - [Inspecting a precode yourself](#inspecting-a-precode-yourself)
- [Project layout](#project-layout)
- [Walking the GC heap](#walking-the-gc-heap)
  - [The GC has its own, unexported descriptor](#the-gc-has-its-own-unexported-descriptor)
  - [Generations, segments and regions](#generations-segments-and-regions)
  - [Sizing an object](#sizing-an-object)
  - [The gaps that make a naive walk lie](#the-gaps-that-make-a-naive-walk-lie)
  - [Reading a heap that is moving](#reading-a-heap-that-is-moving)
  - [What is not covered](#what-is-not-covered)
- [Building and testing](#building-and-testing)
- [Platform support](#platform-support)

---

## Why this is hard

A managed type is described at runtime by a `MethodTable` (the hot part) and an `EEClass` (the cold
part). `typeof(T).TypeHandle.Value` *is* the address of the type's `MethodTable`, so the structures
are right there — but their layouts are **private implementation details of the runtime**. They
have no public contract and they change between releases.

The usual approach is to hardcode the offsets for one runtime version. That is what this project
originally did, against .NET Core 2.2, and it is why it stopped working: on .NET 10 the field order
differs, `MethodTable` no longer carries a method token at all, the debug/release layout split is
gone, and the "multipurpose slot" scheme it relied on no longer exists.

Hardcoding .NET 10's offsets instead would just move the problem to the next release. Worse, the
failure mode of a wrong offset is not a crash — it is **plausible-looking wrong numbers**.

So ClrSpector does not hardcode offsets. It asks the runtime.

---

## How the inspector works

### The contract descriptor

Since .NET 9, CoreCLR publishes a machine-readable description of its own data structures for
diagnostics tooling (the "cDAC" contract descriptor). It is exported from the runtime library as a
data symbol:

```
$ nm -D --defined-only libcoreclr.so | grep -i contract
00000000006a5460 D DotNetRuntimeContractDescriptor@@V1.0
```

`ContractDescriptor` resolves that symbol, validates it, and parses it once. The symbol points at a
small header:

```c
struct DotNetRuntimeContractDescriptor
{
    uint64_t         magic;               // "DNCCDAC\0"
    uint32_t         flags;               // bit 0: 1 = 64-bit pointers
    uint32_t         descriptor_size;
    const char      *descriptor;          // JSON, not NUL-terminated
    uint32_t         pointer_data_count;
    uint32_t         pad0;
    const uintptr_t *pointer_data;        // addresses of runtime globals
};
```

The runtime library is found next to `System.Private.CoreLib.dll` (`coreclr.dll`,
`libcoreclr.so` or `libcoreclr.dylib` by platform), falling back to
`NativeLibrary.GetMainProgramHandle()` for single-file and self-contained hosts where the runtime
is linked into the host executable.

The JSON payload (about 10 KB on 10.0.4) has four parts:

| Key | Contents |
|---|---|
| `version` / `baseline` | Descriptor format version and baseline name. A version bump is a signal to re-verify. |
| `types` | 101 structure layouts: field name → offset. The reserved key `"!"` is the structure's total size. |
| `globals` | 67 runtime globals, either literal values or references into `pointer_data`. |
| `contracts` | 15 contract names → version, e.g. `RuntimeTypeSystem: 1`. |

A field is either a bare offset or `[offset, "typename"]`. Because the shapes are heterogeneous,
parsing uses an explicit `JsonDocument` walk rather than POCO deserialization; the document is
disposed once the dictionaries are built.

Reading a field then looks like this — note that no offset appears in the code:

```csharp
var layout = ContractDescriptor.Current.GetDataType("MethodTable");

mt.Flags            = reader.ReadUInt  (layout["MTFlags"]);
mt.BaseSize         = reader.ReadUInt  (layout["BaseSize"]);
mt.NumberOfVirtuals = reader.ReadUShort(layout["NumVirtuals"]);
```

For reference, the offsets the runtime reported on 10.0.4 — **illustrative only, the code never
assumes them**:

```
MethodTable (size 64)   MTFlags 0   BaseSize 4   MTFlags2 8   NumVirtuals 12
                        NumInterfaces 14   ParentMethodTable 16   Module 24
                        AuxiliaryData 32   EEClassOrCanonMT 40   PerInstInfo 48

EEClass                 MethodTable 16   FieldDescList 24   MethodDescChunk 32
                        CorTypeAttr 40   InternalCorElementType 48
                        NumInstanceFields 50   NumMethods 52   NumStaticFields 54

MethodDesc (size 16)    Flags3AndTokenRemainder 0   ChunkIndex 2
                        EntryPointFlags 3   Slot 4   Flags 6   CodeData 8

MethodDescChunk (24)    MethodTable 0   Next 8   Size 16   Count 17
                        FlagsAndTokenRange 18
```

Because `MemoryReader` addresses every read by explicit offset rather than sequentially, field
*order* is irrelevant — which is what decouples the decoder from the runtime's layout choices.

### Reading a type

```
typeof(T).TypeHandle.Value
        │
        ▼
   MethodTable ─── ParentMethodTable ──▶ base type's MethodTable
        │
        ├──────── EEClassOrCanonMT (tagged union, low bit)
        │              tag 0 ──▶ EEClass            (this type is canonical)
        │              tag 1 ──▶ canonical MethodTable ──▶ its EEClass
        ▼
     EEClass ─── MethodDescChunk ──▶ chunk ──▶ chunk ──▶ …
                                       │
                                       ▼
                                   MethodDesc[]
```

**The union's tag is one bit wide on .NET 10, not two.** This matters: older runtimes used a
two-bit tag that also defined "invalid" (1) and "indirection" (3) kinds, and reading two bits
misreads every shared instantiation as "invalid". Verified against the live runtime — canonical
types (`object`, `string`, `int`, `int[]`, `object[]`, `int[,]`, `List<T>`, interfaces) tag 0 and
their target's back-pointer returns to itself, while shared instantiations (`string[]`,
`List<string>`, `Dictionary<string,int>`) tag 1 and point at their canonical `MethodTable` —
`string[]` resolving to `object[]`'s.

Type category comes from `MTFlags`. Only two things are needed, and both are
verified against reflection across classes, interfaces, value types, enums, arrays and strings:

- `HasComponentSize` (`0x80000000`) — set for strings and arrays, where the low word of `MTFlags`
  holds the element width instead of flags.
- the category field (`0x000F0000`).

Category tests must use the **group mask** (`0x000C0000`), not equality, because sub-categories
live in the low bits of the field: a single-dimension array sets an extra bit (`int[]` is `0xa`
while `int[,]` is `0x8`), and enums, primitives and nullables are all sub-categories of value type.
Equality alone silently misclassifies them.

### Enumerating methods

The old approach walked from a vtable slot to a precode and sniffed x86 opcodes to find the
`MethodDesc`. That depends on generated code and is fragile. ClrSpector instead walks the
`MethodDescChunk` list, which depends only on published data layout:

```
EEClass.MethodDescChunk ──▶ chunk (follow MethodDescChunk.Next until null)
    real method count = Count + 1
    MethodDescs begin at chunk + sizeof(MethodDescChunk)
```

Each `MethodDesc`'s own byte size varies with its classification, so stepping through a chunk uses
the runtime's classification size table (`MethodDescSizeTable`):

```csharp
classification = Flags & 0x0007
index          = classification | (Flags & 0x0038)   // HasNonVtableSlot | MethodImpl | HasNativeCodeSlot
size           = sizeTable[index]
```

Every step is then **cross-checked** against the `MethodDesc`'s own `ChunkIndex`, which
independently records where it sits (in units of the `MethodDescAlignment` global). If the two
disagree, the walk throws instead of yielding bogus methods:

```csharp
if (method.ChunkIndex * alignment != offset)
    throw new ClrSpectorUnsupportedRuntimeException(...);
```

### Recovering names and signatures

A `MethodDesc` does not store a name or a signature. What is recoverable is a **metadata token**,
and the runtime splits it across two structures: the low bits live on the `MethodDesc` as a token
remainder, the high bits on the owning chunk as a token range.

```csharp
remainderMask = (1 << MethodDescTokenRemainderBitCount) - 1;      // 12 bits on 10.0.4
remainder     = Flags3AndTokenRemainder & remainderMask;
tokenRange    = MethodDescChunk.FlagsAndTokenRange & 0x0FFF;

MetadataToken = 0x06000000 | (tokenRange << bitCount) | remainder;  // 0x06 = mdMethodDef
```

Resolving that token through the declaring module turns a decoded `MethodDesc` back into a readable
signature:

```csharp
var resolved = type.Module.ResolveMethod((int)method.MetadataToken);
// resolved.Name, resolved.GetParameters(), resolved.GetGenericArguments()
```

This doubles as the strongest correctness check in the project: because the token is reassembled
from two separate runtime fields, a listing that matches reflection is hard to produce by accident.
`dotnet run --project src/ClrSpectorConsole` prints exactly that, and decodes **279 of `String`'s
280 methods** with correct overload signatures, operators and `PInvoke`/`FCall` classifications.

### Two traps

Both produce plausible wrong values rather than errors, so both are worth stating outright.

**1. An indirect global holds the *address* of the runtime variable.** For a global written
`"Name": [[index], "pointer"]`, `pointer_data[index]` is the address of the symbol — what to do next
depends on the symbol's native type:

| Symbol kind | Accessor | Why |
|---|---|---|
| pointer variable (`g_pObjectClass`, a `MethodTable*`) | `Globals.Dereference(name)` | needs one dereference |
| array or table (`MethodDescSizeTable`) | `Globals.Address(name)` | the address already *is* the data |

Verified: `Globals.Dereference("ObjectMethodTable")` equals `typeof(object).TypeHandle.Value`,
while the undereferenced value matches nothing.

**2. `MethodDescChunk` stores biased values.** The real method count is `Count + 1` and the real
chunk size is `(Size + 1) * MethodDescAlignment`.

### Failing loudly

The failure this project must avoid is not a crash — it is quietly wrong output. So:

- A type or field the descriptor does not describe raises
  `ClrSpectorUnsupportedRuntimeException`, naming the type, the field, the known fields, and the
  runtime version. There is **no fallback to a hardcoded offset**.
- The magic value is validated, and the descriptor's pointer width must agree with the process,
  before any read happens.
- `RequireContract(name, versions)` rejects a contract version this code was not written against.
- Chunk stepping self-checks against `ChunkIndex`, as above.

---

## How method detouring works

### The problem

To mock a dependency in a test you normally extract an interface, even when the seam exists purely
for the test. `MethodDetour` provides that seam without the interface: it redirects calls to a
concrete, non-virtual method so they run a stand-in instead, and restores the original when
disposed.

### What can be detoured

A method can be reached two different ways, and which one applies decides how it is redirected.
Every row below is exercised by the test suite.

| Method kind | Dispatched through | Supported |
|---|---|---|
| `static` | precode | yes |
| non-virtual instance | precode | yes |
| `virtual` | vtable **and** precode | yes — both are patched |
| `override` | vtable | yes, including through a base-typed reference |
| sealed `override` via abstract base | vtable | yes |
| `abstract` declaration | nothing — no implementation | refused, with an explanation |
| reached by **interface** dispatch | interface stub cache | refused by default — [it cannot be undone](#interface-dispatch-cannot-be-undone) |

`MethodDetour.PatchedTargets` reports which paths a given redirect actually patched
(`Precode`, `Vtable`, or both).

### What does not work: patching the code

The obvious approach is to overwrite the target's machine code with a jump to the replacement. It
does not work, and it fails in a way that looks like success — so it is worth showing.

Taking the target's entry point, making the page writable, and writing an
`mov rax, imm64; jmp rax`:

```
from=0x7923a5d19908 to=0x7923a5d19920
before:       real:x
mprotect rc=0
orig bytes:   ff 25 fa 3f 00 00 4c 8b 15 fb 3f 00
after write:  48 b8 08 99 27 a3 3e 70 00 00 ff e0   ← the write landed
after patch:  real:x                                ← but nothing changed
```

The write demonstrably lands. The call still runs the original. The reason is visible in the
original bytes: `ff 25 …` is `jmp qword [rip+disp32]`, so the entry point is **not the method body**
— it is a stub. Callers reach the real code through that stub, and rewriting the stub's first
instruction is both invasive and beside the point.

**The method body never needs to be found at all.**

### What a precode is

A jitted method is reached through a *precode* — a small stub that stands in for the method's entry
point and forwards to wherever the real code currently lives. It exists because the real code moves:
the method may not be jitted yet, and tiered compilation may replace it later. The precode gives
callers one stable address to call.

On x64 that stub is a single rip-relative jump through one pointer-sized slot:

```
entry point ──▶  ff 25 fa 3f 00 00        jmp qword [rip+0x3ffa]
                                                      │
                                          ┌───────────┘
                                          ▼
                              dispatch slot: ──▶ current real code
```

That slot is the interception point for every **non-virtual** call. (Virtual calls take a
different route — see below.)

The runtime publishes its own precode constants in the contract descriptor, and
`PrecodeMachineInfo` exposes them rather than guessing:

```
PrecodeMachineDescriptor @0x742689022fb8  fixup=2  stub=3  invalid=255
                         fixupCodeOffset=6  stubPrecodeSize=24  codePageSize=16384
```

`fixupCodeOffset=6` independently confirms the 6-byte jump length used below, and
`codePageSize=16384` explains where the slot lands: the decoded displacement puts it exactly
`0x4000` past the stub, because the dispatch slots live on a writable data page one code page away
from the executable stub page. That relationship is asserted in the tests.

### Finding the dispatch slot

Decode the one instruction. `MethodDetour` refuses anything that is not this shape rather than
guessing:

```csharp
var code = (byte*)entryPoint;

if (code[0] != 0xFF || code[1] != 0x25)
    throw new MethodDetourException(
        $"'{Describe(target)}' does not begin with a rip-relative jump …");

var displacement = *(int*)(code + 2);

return (IntPtr*)(code + 6 + displacement);   // 6 = length of the jmp instruction
```

The displacement is relative to the address of the *next* instruction, which is why the `+ 6`
appears before adding it.

Both methods are jitted first with `RuntimeHelpers.PrepareMethod`, since an entry point is
meaningless until the method has one.

### Virtual calls go through the vtable instead

A virtual call never consults the precode. It reads the target straight out of the receiver's
MethodTable vtable, which the runtime has already backpatched to the real code:

```
non-virtual / static call ──▶ precode stub ──▶ dispatch slot ──▶ real code
                                                    ▲
                                   patching here catches these calls

virtual call ──▶ receiver's MethodTable ──▶ vtable[slot] ──▶ real code
                                                 ▲
                              …but virtual calls never look at the slot above
```

So redirecting the precode alone leaves every virtual call running the original — and it does so
**silently**, with no error. That was measured before this was fixed: `virtual`, `override`, sealed
`override` and interface-implementing methods all reported "not redirected" while non-virtual and
static worked.

`MethodDetour` therefore patches **both** paths whenever both exist. Patching the vtable alone
would miss a virtual method invoked non-virtually — `base.M()`, or a call the JIT devirtualized —
so both are needed, not either.

That the vtable holds real code rather than the precode entry point is asserted directly:

```csharp
var vtableSlotValue = new MemoryReader(MethodVtable.FindSlot(method)).ReadIntPtr(0);

await Assert.That(vtableSlotValue).IsNotEqualTo(precode.EntryPoint);      // not the stub
await Assert.That(vtableSlotValue).IsEqualTo(precode.DispatchTarget);     // the real code
```

### Where the vtable lives, and why it is chunked

The vtable is **not** one contiguous array. It is an array of *chunk pointers* beginning
immediately after the MethodTable's fixed fields, with **8 slots per chunk**:

```
MethodTable
 +0                       fixed fields (MTFlags, BaseSize, …)
 +MethodTable.Size   ┌──▶ chunk pointer [0] ─────┐
                     │    chunk pointer [1] ──┐  │
                     │    …                   │  │
                     └── (slot / 8) selects   │  │
                                              │  └──▶ chunk: 8 slots, (slot % 8) selects
                                              └─────▶ chunk: 8 slots
```

```csharp
chunkPointer = *(IntPtr*)(methodTable + MethodTable.Size + (slot / 8) * IntPtr.Size);
slotAddress  = chunkPointer + (slot % 8) * IntPtr.Size;
```

The offset of the chunk-pointer array is the descriptor-published `MethodTable` size, so it is not
hardcoded. The 8-slots-per-chunk figure is a CoreCLR compile-time constant that the descriptor does
*not* publish; it is verified against types with more than one chunk, and exposed as
`MethodVtable.SlotsPerVtableChunk`.

**Chunks are not adjacent, and they are shared.** A derived type that overrides nothing in a chunk
reuses its base type's chunk, which can sit at a *lower* address than the MethodTable itself. This
is why treating the vtable as contiguous is wrong rather than merely inelegant — measured on a
subclass overriding only a late slot:

```
Sub MethodTable 0x7dd8e6cc3600   numVirtuals=16
  chunk 0 -> 0x7dd8e6cc3518   (MethodTable-232)   ← shared with the base type
  chunk 1 -> 0x7dd8e6cc3650   (MethodTable+80)

  name  slot   contiguous model      chunked model
  V0    4      MATCH                 MATCH
  V5    9      MATCH                 MATCH
  V9    13     MATCH                 MATCH
  V11   15     wrong address         MATCH        ← the overridden slot
```

The two models agree only when chunks happen to be laid out adjacently, which is why a naive
implementation can pass casual testing and still be wrong.

Chunk sharing has a visible consequence: a vtable patch applies to the **declaring type**, and
subclasses that inherit the slot unchanged are affected too. Subclasses that *override* have their
own slot and are unaffected — asserted by
`RedirectingTheBaseLeavesAnOverridingSubclassAlone`.

The slot index itself comes from the inspector: the decoded `MethodDesc` for the method carries its
`SlotNumber`, matched by metadata token. So the detour is built on the descriptor-driven decoding
described earlier.

### Performing the swap

Each redirect is one pointer-sized store, applied to every dispatch path the method has. The
method's machine code is never modified:

```csharp
private static Patch Apply(IntPtr address, IntPtr value)
{
    CodeProtection.MakeWritable(address, IntPtr.Size);

    var original = *(IntPtr*)address;      // remember
    *(IntPtr*)address = value;             // redirect

    return new Patch(address, original);
}
```

`CodeProtection` makes the page writable with `mprotect` (POSIX) or `VirtualProtect` (Windows),
page-aligning the address first.

Note the replacement's *own* entry point is stored — its precode, not raw code — so the call chain
becomes `target precode slot → replacement precode → replacement code`. That is more robust than
pointing at a code address directly.

Restoring replays every patch in reverse, and `Dispose` is idempotent:

```csharp
public void Dispose()
{
    if (!this.IsActive) return;

    foreach (var patch in this.patches)
        patch.Undo();

    this.IsActive = false;
}
```

Because it is an `IDisposable`, a `using` block restores on every path out — including an
exception, which is what you want when a test fails partway through.

### Why it catches every call shape

For a non-virtual method there is one slot and every caller goes through it, so the redirect is not
specific to how the call is written. All three of these are asserted:

```csharp
using (MethodDetour.Redirect(method, replacement))
{
    service.GetPrice("x");                         // direct call      → proxy
    viaDelegate(service, "x");                     // open delegate    → proxy
    method.Invoke(service, new object[] { "x" });  // reflection       → proxy
}
```

For a virtual method, patching precode *and* vtable covers the virtual call, the non-virtual
`base.M()` call, and a devirtualized call alike.

### Interface dispatch cannot be undone

One case is genuinely not supportable, and it is the sharpest edge here.

Interface dispatch does not read the class vtable directly — it resolves through a dispatch stub
and **caches the result**. That cache is not reverted when the detour is disposed, so a call made
through an interface reference while redirected leaks the proxy *permanently and process-wide*.
Measured:

```
concrete before : real
interface during: PROXY
concrete after  : real       ← the vtable and precode were restored correctly
interface after : PROXY      ← but interface dispatch still resolves to the proxy

fresh instance, after restore:
interface on new instance: PROXY     ← even objects created later
```

Restoring the vtable and precode is not enough, and there is no supported way to flush that cache.
In a test suite this is the worst possible failure: silent contamination of every later test.

So `MethodDetour` **refuses** to redirect a method that implements an interface member:

```
'Exporter.Export' implements an interface method. A call made through an interface reference
while redirected is cached by the runtime's interface dispatch and is NOT undone on dispose -
the redirect leaks permanently and process-wide, reaching even instances created afterwards.
Redirect the interface method itself, or pass allowInterfaceDispatch: true if you are sure the
method is never called through an interface reference.
```

If the method genuinely is never called through an interface reference, the guard can be lifted
knowingly:

```csharp
MethodDetour.Redirect(target, replacement, allowInterfaceDispatch: true);
```

That works and restores correctly — as long as the interface path is never exercised. If a type has
an interface, mocking through the interface is the better tool.

### Keeping it safe

A mismatched replacement would corrupt the stack, so pairings are validated up front and refused
with `MethodDetourException` rather than producing undefined behaviour at the call. Return types
must match, and so must the **effective** parameter lists — where "effective" accounts for an
instance method receiving its declaring type as a leading `this` (by reference, when that type is
a struct). Generic methods, methods on generic types, varargs methods and methods on value types
are refused outright: each needs a hidden argument, or has an entry point, that a redirect cannot
honour.

A refused redirect leaves the target callable — also asserted.

### The hidden return buffer

Matching parameter lists are not enough, because they are not the whole frame. Arguments are
passed in this order:

```
[this] [return buffer] [generics context | varargs cookie] [user arguments]*
```

A return value too large for a register is written through a hidden pointer the caller supplies,
and on x64 that pointer is an ordinary argument sitting **after** `this`. So an instance method
returning a `decimal` receives `(this, returnBuffer, sku)`, while a static stand-in taking the
instance first receives `(returnBuffer, instance, sku)`. Everything is shifted by one: the
instance is reinterpreted as a buffer, and the return value is written over the target object.

Measured, with a static stand-in patched directly into a `decimal`-returning instance method:

```
[retbuf] redirected: result=0 (want 42)                 <- caller's buffer never written
[retbuf] marker=0x2a (want 0x1122334455667788)          <- 42 written INTO the target object
Fatal error. Internal CLR error.                        <- next GC dies on the trampled object
```

arm64 escapes this: it has a dedicated return-buffer register (`x8`) outside the argument
sequence, so nothing shifts.

The fix is a generated **thunk**, described below.

### Thunks: proxy objects, and repairing the shift

Two pairings cannot occupy a dispatch slot as they are, and both are wired up through a small
generated adapter instead:

| Pairing | Why it needs an adapter |
|---|---|
| `AbiShim` | A static stand-in for an instance method whose return value travels in a hidden buffer — the shift above. |
| `ReceiverShift` | An **instance** stand-in: a proxy object, whose own receiver has to come from somewhere. A slot holds a code address and nothing else. |

The adapter is emitted as **IL and compiled by the JIT**, never as hand-written machine code. The
argument shuffle then comes from the same compiler that produced both call frames, so return
buffers, floating-point registers, spilling to the stack and x64-versus-arm64 differences are all
handled by something that already knows the answer.

It is emitted as an **instance** method whenever the target is one, so the adapter's receiver
occupies the same slot as the target's and nothing behind it moves. That is also why
`DynamicMethod` cannot serve here: it is always static, which is precisely the broken shape.
`TypeBuilder` it is — which also yields a real `MethodHandle`, so no private reflection is needed
to find an entry point, and non-collectible code, so the adapter cannot be freed while a slot
still points at it.

`detour.Pairing`, `detour.UsesThunk` and `detour.ThunkEntryPoint` report which path was taken.

### Limits you must know

These are properties of the technique, not gaps in the implementation. Each is documented on the
type itself.

| Limit | Why | What to do |
|---|---|---|
| **Inlined calls cannot be intercepted** | If the JIT inlined the callee, no call happens at all, so there is no dispatch to redirect. | Mark redirectable methods `[MethodImpl(MethodImplOptions.NoInlining)]`. |
| **Tiered compilation rewrites the same slot** | Promoting a method to optimised code updates the dispatch slot, silently dropping your redirect. | Set `<TieredCompilation>false</TieredCompilation>` in test projects that rely on this. |
| **The slot is process-wide** | Two tests redirecting the same method concurrently each undo the other. | Serialize them. With TUnit, `[NotInParallel]`. |
| **Interface dispatch leaks** | Interface dispatch caches the resolved target and the cache is not reverted. Permanent and process-wide. | Refused by default; see above. Mock through the interface instead. |
| **A vtable patch is per declaring type** | Subclasses inheriting the slot are affected; overriding subclasses are not. | Redirect the type whose behaviour you mean to replace. |
| **x64 / POSIX verified only** | The `jmp` decoding is x64-specific and `mprotect` is POSIX. | The Windows `VirtualProtect` path exists but is untested; arm64 is unverified. |
| **Not for production** | Mutating runtime dispatch state is not thread-safe against concurrent calls to the target. | Use it in tests. |

The parallelism limit is not theoretical — it surfaced as a real test failure while building this,
with two tests fighting over one slot.

### Writing a proxy

A **static** replacement whose first parameter stands in for the instance is the simplest shape,
and the only one that reaches the target's slot directly:

```csharp
// production code: concrete, no interface, nothing virtual
public class PriceService
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public decimal GetPrice(string sku) => LookUpFromDatabase(sku);
}

// the stand-in: static, with a leading parameter for the instance
public static class PriceServiceProxy
{
    public static int Calls;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static decimal GetPrice(PriceService instance, string sku)
    {
        Calls++;
        return 42m;
    }
}

[Test]
[NotInParallel]
public async Task UsesThePriceFromTheService()
{
    using (MethodDetour.Redirect(
               typeof(PriceService), nameof(PriceService.GetPrice),
               typeof(PriceServiceProxy), nameof(PriceServiceProxy.GetPrice)))
    {
        await Assert.That(new PriceService().GetPrice("abc")).IsEqualTo(42m);
        await Assert.That(PriceServiceProxy.Calls).IsEqualTo(1);
    }
}
```

`MethodDetour.Redirect` also takes `MethodBase` overloads directly when you need to pick a specific
overload.

### Writing a proxy with state

When the stand-in needs state of its own, write it as an ordinary **instance** method whose
parameters match what the target receives, and pass the object alongside it. A generated thunk
supplies the receiver:

```csharp
public class PriceServiceProxy
{
    public readonly List<string> Seen = new List<string>();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public decimal GetPrice(PriceService instance, string sku)
    {
        this.Seen.Add(sku);                    // the proxy's own state

        return 42m + instance.FixedPrice;      // and the real instance
    }
}

var proxy = new PriceServiceProxy();

using (MethodDetour.Redirect(
           typeof(PriceService), nameof(PriceService.GetPrice),
           proxy, nameof(PriceServiceProxy.GetPrice)))
{
    await Assert.That(new PriceService().GetPrice("abc")).IsEqualTo(142m);
}

await Assert.That(proxy.Seen).IsEquivalentTo(new[] { "abc" });
```

A delegate carries its receiver, so a method group or a closure works too — useful for a one-line
stand-in over captured state:

```csharp
var captured = 7m;

using (MethodDetour.Redirect(
           typeof(PriceService).GetMethod(nameof(PriceService.GetPrice)),
           (Func<PriceService, string, decimal>)((instance, sku) => captured)))
{
    ...
}
```

The proxy is bound to the redirect, not baked into the adapter, so disposing releases it rather
than leaking it for the life of the process. The adapter code itself is emitted once per distinct
pairing and is never reclaimed.

### Inspecting a precode yourself

The precode and vtable machinery is public, so you can look at what a method's dispatch actually
looks like without performing a redirect:

```csharp
var precode = MethodPrecode.Of(typeof(Svc).GetMethod("Virt"));

precode.EntryPoint        // 0x74260af7bb10  - the stable entry point (the stub, not the body)
precode.HexBytes          // "ff 25 fa 3f 00 00 4c 8b 15 fb 3f 00 00 ff 25 fd"
precode.IsRipRelativeJump // true
precode.Disassembly       // "jmp qword [rip+16378]"
precode.DispatchSlot      // 0x74260af7fb10  - the one slot a redirect writes to
precode.DispatchTarget    // 0x74260a9d9300  - where it currently points
```

`ToString()` gives the whole picture on one line, which is handy in a debugger or a log:

```
Svc.Virt entryPoint=0x74260af7bb10 [ff 25 fa 3f 00 00 …] jmp qword [rip+16378]
         slot=0x74260af7fb10 -> 0x74260a9d9300
```

The runtime's own precode constants:

```csharp
var machine = PrecodeMachineInfo.Current;

machine.FixupPrecodeType     // 2
machine.StubPrecodeType      // 3
machine.InvalidPrecodeType   // 255
machine.FixupCodeOffset      // 6      - matches the rip-relative jump length
machine.StubPrecodeSize      // 24
machine.StubCodePageSize     // 16384  - the stub-to-slot distance
```

And the vtable side:

```csharp
MethodVtable.FindSlotNumber(method)   // vtable slot index, or -1 if it occupies none
MethodVtable.FindSlot(method)         // the slot's address, or IntPtr.Zero
MethodVtable.SlotsPerVtableChunk      // 8
```

A live detour also reports what it did:

```csharp
using var detour = MethodDetour.Redirect(target, replacement);

detour.PatchedTargets   // Precode | Vtable
detour.VtableSlot       // patched slot address, or IntPtr.Zero for non-virtual
detour.Precode          // the MethodPrecode above
detour.IsActive         // false after Dispose
```

---

## Project layout

```
src/ClrSpector/                     the library
  Cdac/                             the runtime's self-description
    ContractDescriptor.cs           resolve the export, validate, parse the JSON
    GcContractDescriptor.cs         find the GC's unexported descriptor by scanning
    ProcessMemoryRegions.cs         what is mapped, so a scan or a walk cannot fault
    DataType.cs / FieldLayout.cs    a structure layout: field → offset, plus size
    Globals.cs                      literal and pointer-data globals
  Detours/
    MethodDetour.cs                 redirect a method, restore on dispose
    MethodPrecode.cs                a method's precode and its dispatch slot
    MethodVtable.cs                 locate a virtual method's vtable slot
    PrecodeMachineInfo.cs           the runtime's own precode constants
    CodeProtection.cs               mprotect / VirtualProtect
  ClrObject.cs                      entry point: ClrObject.From<T>()
  ClrEEClass.cs                     the cold half of a type
  Methods/ClrMethodTable.cs         the hot half, plus the MethodDescChunk walk
  Methods/ClrMethodDescription.cs   one method, plus token reconstruction
  Methods/MethodDescSizes.cs        MethodDesc sizes, rebuilt from the descriptor
  Gc/
    ClrGcHeap.cs                    entry point, and the object walk
    ClrGeneration.cs                the generation table
    ClrHeapSegment.cs               one segment or region, and its bounds
    ClrHeapLayouts.cs               offsets and rules hoisted out of the walk
    AllocationHoles.cs              the per-thread buffers a walk must step over
    GcWalkScope.cs                  hold off collection, and report if it happened
  ClrHeapObject.cs                  one object instance: its type and its size
  MemoryReader.cs                   offset-addressed reads

src/ClrSpectorConsole/              prints decoded types next to the reflection view
src/ClrSpectorTests/                TUnit tests
```

## Walking the GC heap

`ClrGcHeap` reads the GC's own structures: the generations, the segments (regions, on .NET 11) that
objects are laid out in, and the objects themselves.

```csharp
// Enter the scope FIRST - establishing it collects, which moves objects and
// rebuilds the region lists, so a snapshot taken earlier would be stale.
using var scope = GcWalkScope.Enter();
var heap = ClrGcHeap.Refresh();

Console.WriteLine(heap);
// gc heap "workstation, regions, background," generations=5 segments=23 live=499000

foreach (var generation in heap.Generations)
{
    Console.WriteLine(generation);            // gen4 (POH) segments=1 live=8184
    foreach (var segment in generation.Segments)
        Console.WriteLine("   " + segment);   // segment @0x… gen=4 mem=0x… live=8184 …
}

foreach (var instance in heap.EnumerateObjects(scope))
    Console.WriteLine(instance);              // object @0x… size=40 mt=0x…

scope.ThrowIfInvalidated();
```

It is read-only. Nothing here writes to a GC structure, and nothing should: mutating them from
inside the process being collected corrupts the heap, and there is no in-process primitive that
makes it sound. `GC.TryStartNoGCRegion` is the only supported lever over collection, and
`GcWalkScope` uses it for exactly that.

### The GC has its own, unexported descriptor

The GC heap layouts are **not** in `DotNetRuntimeContractDescriptor`. The GC is pluggable, so its
descriptor cannot be a fixed export of the runtime — and it is not reachable from any export, nor
from any global in the runtime descriptor.

What the runtime does instead is embed one descriptor per GC flavour it was built with, and leave
them in its data section. On .NET 11 x64 there are three `DNCCDAC` headers a few kilobytes apart:

| `.data` RVA | Contracts | `GCIdentifiers` |
|---|---|---|
| `0x45d940` | `GC: c1` (10 types, 45 globals) | `workstation, regions, background,` |
| `0x45d968` | `GC: c1` (11 types, 29 globals) | `server, regions, background, dynamic_heap` |
| `0x460020` | the 32 runtime contracts | — (this one is the export) |

Only one of them describes the GC actually running. `GcContractDescriptor` finds them by scanning
the runtime module's readable regions for the header magic, and picks the one whose
`GCIdentifiers` matches `GCSettings.IsServerGC`. An ambiguous or empty result fails loudly, because
picking the wrong one would not crash — it would report a plausible but wrong heap.

The scan is driven by the operating system's own memory map rather than a fixed window around the
export, because an access violation in a process reading its own internals cannot be caught. It
takes the process down.

**.NET 10 publishes no GC descriptor at all.** Its single descriptor blob describes no segment,
region, generation or heap, and there is no `g_gcDacGlobals` export to fall back on. That is why
heap walking needs .NET 11.

Under a standalone GC (`DOTNET_GCName`, `clrgc.dll`) the descriptors live in that module instead —
it carries two of its own.

### Generations, segments and regions

There are **five** generations, not three: gen0–gen2 are the small object heap, and the two beyond
`MaxGeneration` are the large and pinned object heaps. The count comes from the descriptor's
`TotalGenerationCount` rather than being assumed.

Workstation GC keeps one heap, and the descriptor's globals point straight at that heap's fields,
so the generation table is the array at `GCHeapGenerationTable`'s own address — `Globals.Address`,
not `Dereference`. `MaxGeneration` is the opposite trap: it is an int-sized variable, so it is read
*at* the symbol's address; dereferencing it as a pointer yields nonsense.

Each generation's `StartSegment` heads a `Next` chain of `HeapSegment`. The four bounds nest —
`Mem <= Allocated <= Committed <= Reserved` — and objects live in `[Mem, Allocated)`.

Two kinds of segment break the obvious rules:

- **Frozen segments** (`IsReadOnly`) hold objects baked into a ReadyToRun image, literal strings and
  the like. They are mapped from the image, so they sit *outside*
  `GCLowestAddress`/`GCHighestAddress` and the range check does not apply to them.
- **The ephemeral segment** (`IsEphemeral`) reports `Allocated == Mem` on a live heap, because the
  GC only writes that field back when it collects. Its real end is the GC's `alloc_allocated`
  counter, so that is what bounds the walk there.

### Sizing an object

An object's MethodTable pointer needs the GC's mark and pin bits cleared with
`ObjectToMethodTableUnmask` — an unmasked read gives an address that is wrong for part of every
collection. The size is then `BaseSize`, plus `ComponentCount * ComponentSize` for an array or
string, rounded up to pointer alignment with a three-pointer minimum.

`BaseSize` is measured from the object *header*, not from the MethodTable pointer, which is why a
class with three `long` fields comes out at 40 bytes rather than 32. The walk advances from one
object's MethodTable pointer by exactly this size and lands on the next one's — the same arithmetic
the GC does.

Sizing reads the two MethodTable fields it needs directly rather than going through
`ClrMethodTable.Create`. That decode also resolves the `EEClass`, which is both far more work per
object and not reachable for every MethodTable found on the heap — the `Free` type's, for one. The
full decode is still available lazily on `ClrHeapObject.MethodTable`.

### The gaps that make a naive walk lie

This is the part that matters. The GC hands **each thread** its own zeroed allocation buffer, and
only the part a thread has used holds objects; the rest sits as a run of zeroes in the middle of
the range the walk covers. So a bump walk hits zeroes long before the end of gen0.

Both obvious responses are wrong:

- **Stop at the first gap** and the walk is safe but reports about 13% of the heap.
- **Scan forward past the zeroes** and it reports about 98% — until a thread allocates into that
  buffer while the walk is in progress. Then the object it writes starts at the buffer's own
  pointer, possibly behind where the scan has reached, and resuming there lands mid-object and
  reads a field as a MethodTable.

So the buffers are located up front instead, by walking the runtime's thread list:
`ThreadStore.FirstThreadLink` is a `Thread*`, each thread's successor is at its own `LinkNext`, and
its buffer is at `RuntimeThreadLocals.AllocContext` → `EEAllocContext.GCAllocationContext` →
`GCAllocContext.Pointer`/`Limit`. Skipping `[Pointer, Limit)` exactly gives **98% coverage with no
guessing**.

One detail took a while to find: the unusable span runs a minimum object's worth *past* `Limit` —
the allocator keeps that headroom so an abandoned buffer can always be filled with a free-object
filler. Those bytes are zero too, so a skip that stops at `Limit` exactly lands back in them and
the walk gives up 24 bytes later.

`Thread` has no published size, incidentally, so the readability probe covers exactly the fields
being read. Using the absent size makes every probe zero-length, which reads as unreadable — and
then no buffers are collected at all, silently, and you are back to the 13% case.

### Reading a heap that is moving

Walking a live heap from inside it is not the same as walking a suspended target. The honest limits:

- `GcWalkScope` commits enough memory up front that no collection is needed, so nothing moves. The
  budget can still be exhausted, at which point a collection happens anyway — so the collection
  counts are compared and `CollectionOccurred` / `ThrowIfInvalidated` say whether the results can
  be trusted. `EnumerateObjects(scope)` also abandons the walk as soon as it notices one.
- A region can be **decommitted** underneath the walk when a collection runs, and reading a
  decommitted page is fatal to the process. Every page is checked before it is read, and the answer
  memoised, so it costs one system call per four kilobytes walked rather than one per object.
- The ephemeral segment's contents are genuinely in motion. A boundary the walk cannot make sense
  of there ends the segment; in a settled segment the same thing is a hard error, because there it
  really would mean the layout was misread.
- Don't allocate heavily while enumerating. The walk is a snapshot read, and a consumer that
  allocates per object both eats the no-GC budget and pushes the buffers the walk steps around.

### What is not covered

- **Server GC.** Its descriptor is found and selected correctly, but reading the per-core heaps
  needs the `Heaps`/`NumHeaps` globals and the `GCHeap` type. `ClrGeneration` fails loudly rather
  than pretending; workstation GC only for now.
- **Some regions do not decode.** After repeated forced collections a minority of regions hold data
  the bump walk cannot follow, and the walk raises rather than fabricating objects. What is in them
  is not yet understood — most likely regions on a free list or not yet swept, which would need the
  `GCHeapFreeRegions` and swept-flag state to identify and skip.
- **Roots.** This walks the heap by address, not by reachability. Nothing here enumerates roots or
  says whether an object is live.

## Building and testing

```bash
cd src
dotnet build ClrSpector.sln -warnaserror
dotnet test --project ClrSpectorTests/ClrSpectorTests.csproj
dotnet run  --project ClrSpectorConsole/ClrSpectorConsole.csproj
```

Tests use **TUnit**, which runs on Microsoft.Testing.Platform. Two consequences:

- The .NET 10 SDK needs an opt-in, already present in `global.json`:
  ```json
  { "test": { "runner": "Microsoft.Testing.Platform" } }
  ```
- `dotnet test` takes `--project`, not a bare project path.

The test project sets `<TieredCompilation>false</TieredCompilation>` so entry points stay stable
for the detour tests.

The suite is built around facts obtainable **independently** of the decoder, since those are what
distinguish a correct decode from one that merely does not crash: parent-MethodTable identity
against `typeof(object)`, the `EEClass` back-pointer round-trip, field and method counts against
reflection, reconstructed tokens against `MethodInfo.MetadataToken`, category flags against
`Type.IsValueType`/`IsInterface`/`IsArray`, component sizes against element widths, and the
descriptor's own globals against `typeof(object|string|object[]).TypeHandle.Value`.

## Platform support

Targets **.NET 11**. The type decoding and the GC heap walk are verified on
**.NET 11.0.0-preview.7.26381.103, win-x64**. `Architecture` and `OperatingSystem` are descriptor
globals, so the inspector is portable in principle, but arm64 and macOS are unverified.

The GC heap walk additionally needs the process's memory map, to find the GC descriptor and to
avoid reading a page that has been decommitted. That is implemented for Windows and Linux;
elsewhere it fails loudly rather than guessing. Type decoding and detouring are unaffected.

The contract descriptor is a diagnostics contract — deliberately versioned and far more stable than
raw offsets, but not a public API surface, and it is intended for *out-of-process* use. Reading it
in-process works because the target is the current process. Treat a version bump as a signal to
re-verify, which is what the fail-loud checks exist for.

.NET 11 moved the goalposts twice in ways worth recording, because both are silent traps:

- **Contract versions became strings.** `"ExecutionManager": 2` is now `"ExecutionManager": "c2"`,
  and every contract in the 11.0 descriptor uses that form. Code that called `GetInt32()` on it
  threw. Both encodings are accepted now.
- **`MethodDescSizeTable` was removed.** It was a byte table mapping a MethodDesc's classification
  to its size, and stepping through a `MethodDescChunk` needs that size. It is not a loss of
  information: the table only precomputes "sizeof the concrete MethodDesc subclass, plus the
  optional trailing slots", and the descriptor still publishes the size of every one of those
  types. `MethodDescSizes` reconstructs it. The chunk walk cross-checks every step against each
  MethodDesc's own `ChunkIndex`, which is what proved the reconstruction right.

Also gone in 11.0: the `ObjectHeaderSize` global and the `ArrayClass` and `GCHandle` types.

## Licence

MIT. See [LICENSE](LICENSE).
