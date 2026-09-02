# ClrSpector

Reads CoreCLR's private in-memory data structures from inside the running process, and uses that
to **detour a method call** — swapping a concrete method for a stand-in so it can be mocked
without the production type needing an interface.

Targets **.NET 10**. Verified on .NET 10.0.4 / linux-x64.

```csharp
// Inspect what the runtime knows about a type
var methodTable = ClrObject.From<Order>().MethodTable;
foreach (var method in methodTable.Methods)
    Console.WriteLine(method.MetadataToken);

// Stand in for a concrete, non-virtual method for the duration of a test
using (MethodDetour.Redirect(
           typeof(PriceService), nameof(PriceService.GetPrice),
           typeof(PriceServiceProxy), nameof(PriceServiceProxy.GetPrice)))
{
    Assert.Equal(42m, new PriceService().GetPrice("abc"));  // the proxy answers
}
// original behaviour restored here
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
  - [What does not work: patching the code](#what-does-not-work-patching-the-code)
  - [What a precode is](#what-a-precode-is)
  - [Finding the dispatch slot](#finding-the-dispatch-slot)
  - [Performing the swap](#performing-the-swap)
  - [Why it catches every call shape](#why-it-catches-every-call-shape)
  - [Keeping it safe](#keeping-it-safe)
  - [Limits you must know](#limits-you-must-know)
  - [Writing a proxy](#writing-a-proxy)
- [Project layout](#project-layout)
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

That slot is the interception point. Everything reaching the method goes through it.

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

### Performing the swap

The redirect is one pointer-sized store. The method's machine code is never modified:

```csharp
CodeProtection.MakeWritable((IntPtr)slot, IntPtr.Size);

var original = *slot;                                     // remember
*slot = replacement.MethodHandle.GetFunctionPointer();     // redirect
```

`CodeProtection` makes the page writable with `mprotect` (POSIX) or `VirtualProtect` (Windows),
page-aligning the address first.

Note the replacement's *own* entry point is stored — its precode, not raw code — so the call chain
becomes `target precode slot → replacement precode → replacement code`. That is more robust than
pointing at a code address directly.

Restoring is the same store in reverse, and `Dispose` is idempotent:

```csharp
public void Dispose()
{
    if (!this.IsActive) return;

    *this.slot = this.original;
    this.IsActive = false;
}
```

Because it is an `IDisposable`, a `using` block restores on every path out — including an
exception, which is what you want when a test fails partway through.

### Why it catches every call shape

There is one slot and every caller goes through it, so the redirect is not specific to how the call
is written. All three of these are asserted in the test suite:

```csharp
using (MethodDetour.Redirect(method, replacement))
{
    service.GetPrice("x");                         // direct call      → proxy
    viaDelegate(service, "x");                     // open delegate    → proxy
    method.Invoke(service, new object[] { "x" });  // reflection       → proxy
}
```

### Keeping it safe

A mismatched replacement would corrupt the stack, so pairings are validated up front and refused
with `MethodDetourException` rather than producing undefined behaviour at the call. Return types
must match, and so must the **effective** parameter lists — where "effective" accounts for an
instance method receiving its declaring type as a leading `this`:

```csharp
private static IEnumerable<Type> EffectiveParameters(MethodBase method)
{
    var parameters = new List<Type>();

    if (!method.IsStatic)
        parameters.Add(method.DeclaringType);          // the implicit 'this'

    parameters.AddRange(method.GetParameters().Select(p => p.ParameterType));

    return parameters;
}
```

A refused redirect leaves the target callable — also asserted.

### Limits you must know

These are properties of the technique, not gaps in the implementation. Each is documented on the
type itself.

| Limit | Why | What to do |
|---|---|---|
| **Inlined calls cannot be intercepted** | If the JIT inlined the callee, no call happens at all, so there is no dispatch to redirect. | Mark redirectable methods `[MethodImpl(MethodImplOptions.NoInlining)]`. |
| **Tiered compilation rewrites the same slot** | Promoting a method to optimised code updates the dispatch slot, silently dropping your redirect. | Set `<TieredCompilation>false</TieredCompilation>` in test projects that rely on this. |
| **The slot is process-wide** | Two tests redirecting the same method concurrently each undo the other. | Serialize them. With TUnit, `[NotInParallel]`. |
| **x64 / POSIX verified only** | The `jmp` decoding is x64-specific and `mprotect` is POSIX. | The Windows `VirtualProtect` path exists but is untested; arm64 is unverified. |
| **Not for production** | Mutating runtime dispatch state is not thread-safe against concurrent calls to the target. | Use it in tests. |

The parallelism limit is not theoretical — it surfaced as a real test failure while building this,
with two tests fighting over one slot.

### Writing a proxy

Prefer a **static** replacement whose first parameter stands in for the instance. A differently
typed *instance* replacement would receive a `this` of the wrong type — it may appear to work while
the proxy happens not to touch any fields, and corrupt memory as soon as it does.

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

---

## Project layout

```
src/ClrSpector/                     the library
  Cdac/                             the runtime's self-description
    ContractDescriptor.cs           resolve the export, validate, parse the JSON
    DataType.cs / FieldLayout.cs    a structure layout: field → offset, plus size
    Globals.cs                      literal and pointer-data globals
  Detours/
    MethodDetour.cs                 redirect a method, restore on dispose
    CodeProtection.cs               mprotect / VirtualProtect
  ClrObject.cs                      entry point: ClrObject.From<T>()
  ClrEEClass.cs                     the cold half of a type
  Methods/ClrMethodTable.cs         the hot half, plus the MethodDescChunk walk
  Methods/ClrMethodDescription.cs   one method, plus token reconstruction
  MemoryReader.cs                   offset-addressed reads

src/ClrSpectorConsole/              prints decoded types next to the reflection view
src/ClrSpectorTests/                TUnit tests
```

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

Verified on **.NET 10.0.4, linux-x64**. `Architecture` and `OperatingSystem` are descriptor
globals, so the inspector is portable in principle, but arm64 and Windows are unverified and the
detour is x64/POSIX-specific as noted above.

The contract descriptor is a diagnostics contract — deliberately versioned and far more stable than
raw offsets, but not a public API surface, and it is intended for *out-of-process* use. Reading it
in-process works because the target is the current process. `descriptor.version` is `0` today;
treat a bump as a signal to re-verify, which is what the fail-loud checks exist for.

## Licence

MIT. See [LICENSE](LICENSE).
