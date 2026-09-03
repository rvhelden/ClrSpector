# ClrSpector

Reads CoreCLR's private in-memory data structures from inside the running process, and builds four
things on them:

| | |
|---|---|
| **An inspector** | Types, fields, methods, interfaces, modules, threads, the GC heap — decoded from the runtime's own published layouts rather than hardcoded offsets. |
| **A metadata reader** | Names, signatures, generic constraints and attributes, read out of the mapped image with no `System.Reflection` and no `System.Reflection.Metadata` in the path. |
| **An IL reader and a C# projection** | A method's IL from either a `MethodBase` or a bare MethodDesc, and the same IL written back as C# — faithfully, or with the compiler's scaffolding undone. |
| **A method detour** | Swapping a concrete method for a stand-in, so it can be mocked without the production type needing an interface. |

Targets **.NET 11**. Verified on **.NET 11.0.0-preview.7.26381.103, win-x64**.

```csharp
// What the runtime knows about a type
var table = ClrObject.From<Order>().MethodTable;
Console.WriteLine($"{table.MetadataName}: {table.Methods.Count} methods, {table.Fields.Count} fields");

// A method's IL, read through its MethodDesc - no Type, no MethodBase
var restock = table.FindMethod("Restock");
Console.WriteLine(ClrMethodIl.Of(restock).Dump(IlDumpStyle.Auto));

// The same IL as C#, with the compiler's scaffolding undone
Console.WriteLine(ClrMethodIl.Of(restock).DumpCSharp(IlDumpStyle.Auto, ClrCSharpForm.Structured));

// Stand in for a concrete method for the duration of a test - no interface required
var proxy = new PriceServiceProxy();

using (MethodDetour.Redirect(
           typeof(PriceService), nameof(PriceService.GetPrice),
           proxy, nameof(PriceServiceProxy.GetPrice)))
{
    Assert.Equal(42m, new PriceService().GetPrice("abc"));   // the proxy answers
}
// original behaviour restored here
```

That third line prints this, for a method read out of memory with nothing but its MethodDesc:

```csharp
[Audited("reports availability", AuditLevel.Full, Parts = AuditParts.Outputs)]
[MethodImpl(MethodImplOptions.NoInlining)]
public string Restock<T>(T wanted)
    where T : INumber<T>
{
    T quantity = INumberBase<T>.CreateChecked<int>(this.Quantity);
    int missing = 0;
    for (T i = INumberBase<T>.Zero; i < wanted; i++)
    {
        missing += i < quantity ? 0 : 1;
    }
    try
    {
        return missing == 0 ? "ok" : "short " + missing.ToString();
    }
    catch (InvalidOperationException)
    {
        return "failed";
    }
}
```

Every part of it came from somewhere different: the shape from the IL, the local names from the
module's PDB, their types from the local signature, the `try`/`catch` from the exception table in
the body's data sections, the attributes from the CustomAttribute table **and** the MethodDef
flags, the constraint from the GenericParam table, and `public` from the MethodDef row.

---

## Contents

- [Start here](#start-here)
- [The one idea](#the-one-idea) — why nothing here hardcodes an offset
- [Reading types](#reading-types)
- [Metadata without reflection](#metadata-without-reflection)
- [Attributes](#attributes)
- [Reading IL](#reading-il)
- [Local names, from the PDB](#local-names-from-the-pdb)
- [IL back to C#](#il-back-to-c)
- [Code, dispatch and detours](#code-dispatch-and-detours)
- [The runtime's other structures](#the-runtimes-other-structures)
- [Walking the GC heap](#walking-the-gc-heap)
- [Verification](#verification)
- [Platform support and version traps](#platform-support-and-version-traps)
- [Project layout](#project-layout)

---

## Start here

```bash
cd src
dotnet build ClrSpector.sln -warnaserror
dotnet run --project ClrSpectorConsole            # the tour
cd ClrSpectorTests && dotnet run -c Debug         # the tests
```

`ClrSpectorConsole` is the tour: one short section per capability, each printing real output from
the process it is running in. Reading its `Main` is the fastest way to see what is here.

```
--- runtime and contracts ------------  the descriptor, and what it publishes
--- type layout ----------------------  MethodTable, EEClass, parent, flags
--- field layout ---------------------  offsets, proved by reading a live object
--- methods --------------------------  the MethodDescChunk walk
--- interfaces -----------------------  declared vs implemented, default impls
--- names and IL straight from memory   no Type, no MethodBase
--- IL disassembly -------------------  coloured, from a MethodDesc
--- one method as IL, as C#, and as structured C#
--- modern C# there and back ---------  generics, patterns, unions, filters
--- dispatch: precode and vtable -----  how a call actually gets there
--- an address back to its method ----  the code map
--- tiering --------------------------  what the runtime recompiled
--- detour: a proxy object -----------  mocking without an interface
--- detour: a new method body --------  emitted IL in a live slot
--- async continuations --------------  .NET 11 runtime async on the heap
--- threads --------------------------  the runtime's own ThreadStore
--- an exception's captured frames ---  the trace data, not the string
--- modules, assemblies, loader heaps
--- assembly metadata ----------------  tables, heaps, entries
--- signatures without reflection
--- generics: what metadata cannot tell you
--- attributes without constructing them
--- one object on the heap
--- the GC heap ----------------------  generations, regions, a full walk
```

Every section is wrapped, so one that cannot run reports why and the rest still do — a sample
should not hide twenty working features behind one unsupported one.

### Project settings that matter

| Setting | Needed for | Why |
|---|---|---|
| `<TieredCompilation>false</TieredCompilation>` | detours | Tiering rewrites the same dispatch slot a detour patches, silently dropping it. `MethodDetour` refuses an eligible method rather than letting that happen. |
| `<Features>runtime-async=on</Features>` | the continuation walk | Makes the compiler emit .NET 11 runtime async, which is what produces heap `Continuation` objects instead of state machines. |
| `<LangVersion>preview</LangVersion>` | the `union` in the modern-C# sample | A .NET 11 preview language feature. |
| default portable PDB, or `<DebugType>embedded</DebugType>` | local variable names | Names exist nowhere else. |

Tests use **TUnit**, which runs on Microsoft.Testing.Platform. The SDK opt-in is already in
`global.json`:

```json
{ "test": { "runner": "Microsoft.Testing.Platform" } }
```

---

## The one idea

A managed type is described at runtime by a `MethodTable` (the hot part) and an `EEClass` (the cold
part), and `typeof(T).TypeHandle.Value` *is* the address of the `MethodTable`. The structures are
right there — but their layouts are **private implementation details of the runtime**, with no
public contract, and they change between releases.

The usual approach is to hardcode the offsets for one runtime version. That is what this project
originally did against .NET Core 2.2, and it is why it stopped working: field order differs,
`MethodTable` no longer carries a method token, the debug/release layout split is gone, and the
"multipurpose slot" scheme it relied on no longer exists. Hardcoding .NET 11's offsets instead
would just move the problem to the next release — and the failure mode of a wrong offset is not a
crash, it is **plausible-looking wrong numbers**.

So ClrSpector asks the runtime.

### The contract descriptor

Since .NET 9, CoreCLR publishes a machine-readable description of its own data structures for
diagnostics tooling (the "cDAC" contract descriptor), exported from the runtime library as a data
symbol:

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

The JSON payload (about 10 KB) has four parts:

| Key | Contents |
|---|---|
| `version` / `baseline` | Descriptor format version and baseline name. A version bump is a signal to re-verify. |
| `types` | ~100 structure layouts: field name → offset. The reserved key `"!"` is the structure's total size. |
| `globals` | Runtime globals, either literal values or references into `pointer_data`. |
| `contracts` | Contract names → version, e.g. `RuntimeTypeSystem: 1`. |

A field is either a bare offset or `[offset, "typename"]`. Because the shapes are heterogeneous,
parsing uses an explicit `JsonDocument` walk rather than POCO deserialization; the document is
disposed once the dictionaries are built.

Reading a field then looks like this — note that no offset appears in the code:

```csharp
var runtime = ContractDescriptor.Current;

runtime.Version;                           // descriptor version
runtime.Globals.Text("Architecture");      // "x64"

var layout = runtime.GetDataType("MethodTable");

mt.Flags            = reader.ReadUInt  (layout["MTFlags"]);
mt.BaseSize         = reader.ReadUInt  (layout["BaseSize"]);
mt.NumberOfVirtuals = reader.ReadUShort(layout["NumVirtuals"]);
```

For reference, the offsets the runtime reported — **illustrative only, the code never assumes
them**:

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

`GcContractDescriptor` is the same idea for the GC, which publishes its own descriptor separately
and does not export it at all — see [the GC heap](#walking-the-gc-heap).

### Two traps

Both produce plausible wrong values rather than errors.

**1. An indirect global holds the *address* of the runtime variable.** For a global written
`"Name": [[index], "pointer"]`, `pointer_data[index]` is the address of the symbol — what to do
next depends on the symbol's native type:

| Symbol kind | Accessor | Why |
|---|---|---|
| pointer variable (`g_pObjectClass`, a `MethodTable*`) | `Globals.Dereference(name)` | needs one dereference |
| array or table | `Globals.Address(name)` | the address already *is* the data |

Verified: `Globals.Dereference("ObjectMethodTable")` equals `typeof(object).TypeHandle.Value`,
while the undereferenced value matches nothing.

**2. `MethodDescChunk` stores biased values.** The real method count is `Count + 1` and the real
chunk size is `(Size + 1) * MethodDescAlignment`.

### Failing loudly

The failure this project must avoid is not a crash — it is quietly wrong output. So:

- A type or field the descriptor does not describe raises `ClrSpectorUnsupportedRuntimeException`,
  naming the type, the field, the known fields, and the runtime version. There is **no fallback to
  a hardcoded offset**.

  ```
  The contract descriptor does not publish a size for 'AsyncResumeInfo'.
  Runtime: .NET 11.0.0-preview.7.26381.103 / win-x64 / pointer size 8
  ```

- The magic value is validated, and the descriptor's pointer width must agree with the process,
  before any read happens.
- `RequireContract(name, versions)` rejects a contract version this code was not written against.
- Every walk that can be cross-checked is: chunk stepping against each MethodDesc's own
  `ChunkIndex`, field offsets against a value written and read back, IL instruction lengths against
  the body size.

---

## Reading types

```csharp
var table = ClrObject.From<Order>().MethodTable;     // or ClrObject.From(someType)

table.MetadataName;             // "ClrSpectorConsole.Order", from the string heap
table.Name;                     // via the type handle - reports the full instantiation
table.ParentMethodTable?.Name;
table.NumberOfVirtuals;
table.ContainsGcPointers;
table.HasInstantiation;         // a constructed generic
table.TypeDefToken;
table.Metadata;                 // the module's metadata
```

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

**The union's tag is one bit wide, not two.** Older runtimes used a two-bit tag that also defined
"invalid" (1) and "indirection" (3), and reading two bits misreads every shared instantiation as
"invalid". Verified against the live runtime: canonical types (`object`, `string`, `int`, `int[]`,
`object[]`, `int[,]`, `List<T>`, interfaces) tag 0 and their target's back-pointer returns to
itself, while shared instantiations (`string[]`, `List<string>`, `Dictionary<string,int>`) tag 1
and point at their canonical `MethodTable` — `string[]` resolving to `object[]`'s.

Type category comes from `MTFlags`, and category tests must use the **group mask** (`0x000C0000`),
not equality: sub-categories live in the low bits of the field, so a single-dimension array sets an
extra bit (`int[]` is `0xa` while `int[,]` is `0x8`), and enums, primitives and nullables are all
sub-categories of value type. Equality alone silently misclassifies them. `HasComponentSize`
(`0x80000000`) is set for strings and arrays, where the low word of `MTFlags` holds the element
width instead of flags.

### Fields, and proving an offset is real

`EEClass.FieldDescList` holds the type's own instance fields followed by its statics. Each
`FieldDesc` packs a token plus storage flags into one word, and a 27-bit offset plus a 5-bit
element type into another:

```csharp
foreach (var field in table.Fields)
    Console.WriteLine($"+{field.Offset,-3} {field.Name} : {field.ElementType}" +
                      $"{(field.IsStatic ? " static" : string.Empty)}" +
                      $"{(field.IsThreadStatic ? " [ThreadStatic]" : string.Empty)}");
```

```
First        offset=4    type=I4     reads 0x11111111
Second       offset=16   type=I8     reads 0x2222222222222222
Text         offset=8    type=CLASS  reads ptr 0x25fbe843b88
Small        offset=24   type=U1     reads 0x33
StaticField  offset=0    type=I4     static
PerThread    offset=16   type=I4     threadstatic
```

The runtime **reordered** them — `Text` sits between `First` and `Second`. Reflection reports
declaration order and no offsets at all; this is where the fields actually are, and the tests prove
it by writing a known value into a live object and reading it back at the reported offset.

`table.Fields` is what the type declares, not what it inherits;
`table.DeclaredInstanceFieldCount` counts the instance ones.

### Methods, and the chunk walk

The old approach walked from a vtable slot to a precode and sniffed x86 opcodes to find the
`MethodDesc`. That depends on generated code. ClrSpector walks the `MethodDescChunk` list instead,
which depends only on published layout:

```
EEClass.MethodDescChunk ──▶ chunk (follow MethodDescChunk.Next until null)
    real method count = Count + 1
    MethodDescs begin at chunk + sizeof(MethodDescChunk)
```

Each `MethodDesc`'s own byte size varies with its classification and which optional slots trail it:

```csharp
classification = Flags & 0x0007                 // -> sizeof(MethodDesc | FCallMethodDesc | ...)
size           = baseSizeOf(classification)
if (Flags & 0x0008) size += sizeof(NonVtableSlot)      //  8
if (Flags & 0x0010) size += sizeof(MethodImpl)         // 16
if (Flags & 0x0020) size += sizeof(NativeCodeSlot)     //  8
if (Flags & 0x0040) size += sizeof(AsyncMethodData)    // 24  <- .NET 11
```

Every size on the right comes from the descriptor, so none of those numbers are compiled in.
`AsyncMethodData` is new in .NET 11's runtime async work and is the largest of the four; omitting
it undercounts every async method's `MethodDesc` and desynchronises the rest of its chunk. That was
measured: 68 of ~2,500 CoreLib types failed to walk, every `Task`-related type among them.

Every step is then **cross-checked** against the `MethodDesc`'s own `ChunkIndex`, which
independently records where it sits, in units of the `MethodDescAlignment` global:

```csharp
if (method.ChunkIndex * alignment != offset)
    throw new ClrSpectorUnsupportedRuntimeException(...);
```

```csharp
foreach (var method in table.Methods)
    Console.WriteLine($"slot {method.SlotNumber}  {method.Name}  {method.Signature}");

var describe = table.FindMethod("Describe");        // by name, MethodBase, or token
```

### Reconstructing a method's token

A `MethodDesc` stores no name and no signature. What is recoverable is a **metadata token**, and
the runtime splits it across two structures: the low bits live on the `MethodDesc` as a token
remainder, the high bits on the owning chunk as a token range.

```csharp
remainderMask = (1 << MethodDescTokenRemainderBitCount) - 1;      // 12 bits
remainder     = Flags3AndTokenRemainder & remainderMask;
tokenRange    = MethodDescChunk.FlagsAndTokenRange & 0x0FFF;

MetadataToken = 0x06000000 | (tokenRange << bitCount) | remainder;  // 0x06 = mdMethodDef
```

This doubles as the strongest correctness check in the project: because the token is reassembled
from two separate runtime fields, a listing that matches reflection is hard to produce by accident.
Decoding `String` this way yields **279 of its 280 methods** with correct overload signatures,
operators and `PInvoke`/`FCall` classifications.

### Reaching a method without reflection

A MethodDesc address is exactly what a `RuntimeMethodHandle` wraps. That is the bridge: anything
the runtime can do with a handle is reachable from a MethodDesc with no `Type` or `MethodBase`
involved.

```csharp
var method = table.FindMethod("Describe");

method.Handle          // RuntimeMethodHandle
method.Prepare()       // jits it, no MethodInfo needed
method.EntryPoint      // the prepared entry point
method.ReadIl()        // its IL, out of the module image

MethodPrecode.Of(method);        // the precode
MethodVtable.FindSlot(method);   // the vtable slot
ClrMethodIl.Of(method);          // disassembly
```

Verified: the entry point from the handle is bit-identical to reflection's, and so are the precode
and the vtable slot. The MethodDesc route to the vtable slot is *better* than reflection's, not
merely equivalent — a MethodDesc records its own slot number, so nothing has to match metadata
tokens across the type's methods to find it.

One thing genuinely needs reflection: comparing two methods' **signatures**, which the detour's
pairing check and the body emitter both do. A MethodDesc does not carry parameter types, so those
paths resolve back through `ClrMethodDescription.Method` and say so.

### Interfaces, and default implementations

The runtime builds a full interface map on every MethodTable, but the contract publishes only
`NumInterfaces` — the count — and **no pointer to the map**. The runtime's own cDAC reader has the
same limitation. So the interfaces come from metadata, and each is resolved back to its own
MethodTable through the module's lookup maps, at which point everything else here applies to it
unchanged:

```csharp
foreach (var implemented in table.DeclaredInterfaces)
{
    var iface = implemented.Interface;          // its own MethodTable

    foreach (var method in iface.Methods)
        Console.WriteLine($"{method.Name}: {(method.HasBody ? "default impl" : "abstract")}");
}
```

```
AbiProbe.ILocal @0x7ffa29a94198
    Required       abstract
    Defaulted      default impl
        IL_0000:  ldarg.1
        IL_0001:  ldc.i4.2
        IL_0002:  mul
        IL_0003:  ret
System.IDisposable @0x7ffa29a33f60
    Dispose        abstract
System.IComparable<AbiProbe.Thing> (constructed generic - no MethodTable here)
```

**A default implementation is just a body on an interface method**, so `HasBody` answers it from
the metadata RVA without reading the IL. Three resolution outcomes, each reported rather than
glossed:

| Declared as | Resolved through | Result |
|---|---|---|
| TypeDef (same module) | `TypeDefToMethodTableMap` | full MethodTable |
| TypeRef (another module) | `TypeRefToMethodTableMap` | full MethodTable |
| TypeSpec (constructed generic) | — | named from its signature, no MethodTable |

`DeclaredInterfaces` is what the type's own metadata row declares, which is not always
`NumberOfInterfaces`: a class inheriting an interface from its **base class** declares nothing
itself. Measured — a derived class declared 0 where the runtime counted 1. Both numbers are right
about different questions.

---

## Metadata without reflection

`ClrModuleMetadata` walks the mapped image's own PE headers to the metadata root, and
`MetadataImage` parses the tables and heaps in place. Nothing here depends on any of
`System.Reflection`, and nothing is copied — rows, names and blobs are read out of the mapped bytes.

```csharp
var metadata = table.Metadata;         // or ClrModuleMetadata.Of(module) / .AtImageBase(imageBase)

metadata.FullTypeName(table.TypeDefToken);        // "ClrSpectorConsole.Order"
metadata.MethodName(method.MetadataToken);
metadata.FieldName(field.MetadataToken);
metadata.TokenName(0x0A000123);                   // whatever an IL operand's token names
metadata.UserString(0x70000001);                  // a ldstr literal
metadata.MethodBodyRva(method.MetadataToken);

var image = metadata.Image;
image.RowCount(MetadataTable.MethodDef);
image.ReadColumn(MetadataTable.TypeDef, rowId: 1, column: 1);
image.String(index);
image.Blob(index);                                // a SignatureBlob, read in place
image.IsSorted(MetadataTable.CustomAttribute);    // whether a binary search is legitimate
```

Because the image is *mapped*, an RVA is simply an offset from the module base, so no section
translation is needed.

The awkward part of ECMA-335 is that a row's address is not a fixed offset: every column is two or
four bytes depending on how big the thing it points at is — a heap index widens once the heap
passes 64 KB, a table index once that table passes 65,535 rows, and a coded index once the largest
table it can address does. The width of a column in one table therefore depends on the row counts
of others, so every table is measured before any row of a later table can be found. That is why the
whole schema is here rather than only the tables a signature needs.

The two name routes differ deliberately: `Name` goes through the type handle and reports the full
instantiation, `List<string>`; `MetadataName` reports what the TypeDef row holds, ``List`1``. A
nested type comes back as `Outer+Nested` either way, which costs a walk to the enclosing row.

### Signatures

```csharp
var signature = ClrMethodSignature.Of(method);

signature.ReturnType;              // ClrSignatureType: element type, generic args, rank, modifiers
signature.HasThis;
signature.Parameters;              // names from the Param table, types from the blob
signature.IsGeneric;
signature.RequiredParameterCount;  // differs from Parameters.Count only for varargs
```

Names and types come from two different places on purpose. The blob carries the type and has no
names; the name and the direction live in the Param table — a signature alone renders `out double`
as `ref double`, correctly and uselessly.

`TokenSignature(token)` does the same for whatever a **call site** names — MethodDef, MemberRef,
MethodSpec or StandAloneSig. That is what makes it possible to model a call read from memory: you
cannot know how much of the evaluation stack a `call` consumes without its parameter count and its
this-ness, and both live in the signature blob.

`LocalSignature(token)` decodes a body's locals; a MethodSpec is named by decoding its
instantiation, so a call to a generic method reads as
`System.Runtime.CompilerServices.AsyncHelpers::Await<int>` rather than as a bare token.

### Generic parameters and constraints

```csharp
foreach (var parameter in metadata.GenericParameters((int)method.MetadataToken))
    Console.WriteLine(parameter);        // "T : INumber<T>", "TKey : class, new()"
```

Constraints are the clearest case of something that is metadata rather than code. They change what
the compiler may emit — `new()` is why `new T()` becomes a call to `Activator.CreateInstance<T>()`,
and an interface constraint is why a call on a type parameter can be a *constrained* call at all —
but no instruction in the body says so. A signature refers to its parameters by position (`!0` for
a type's, `!!0` for a method's) because that is all a signature holds; the names are in the
GenericParam table, which is the only place `T` exists.

---

## Attributes

`GetCustomAttributes` is not a read. It reads the metadata row, then **constructs the attribute** —
running that attribute's constructor, in your process — and hands you the instance. So it needs the
attribute's type to load, and its constructor to be willing to run. What the assembly actually
holds is a `CustomAttribute` row: who it was applied to, which constructor was named, and a blob of
the arguments. Reading the row runs nothing.

```csharp
foreach (var attribute in table.CustomAttributes)
    Console.WriteLine(attribute);       // [Audited("regulated", AuditLevel.Full, ...)]

table.Fields.First(f => f.Name == "Quantity").CustomAttributes;
table.FindMethod("Describe").CustomAttributes;
ClrAssembly.Of(typeof(object)).CustomAttributes;      // [assembly: ...]
metadata.ModuleAttributes;                            // [module: ...]
metadata.AllCustomAttributes;                         // every row in the module
```

Any token the `HasCustomAttribute` coded index can carry works — a type, a method, a field, a
parameter, a generic parameter, the module row, the assembly row — so there is one method rather
than one per kind of member. Each argument reports more than its value: its position and parameter
name, or the field or property it named, how it is stored, and the address and length of the blob
it came out of.

Verified against `GetCustomAttributesData` — which reports the same as-written view rather than a
constructed instance — on a type exercising every encoding, and on a spread of CoreLib members: no
mismatches. All **30,034** `CustomAttribute` rows in CoreLib decode.

### What the blob does not tell you

The encoding (ECMA-335 II.23.3) is small and mostly obvious, with three places where the obvious
reading is wrong.

**Positional arguments carry no types.** The blob is bare values in their natural widths, little
-endian and unaligned. Their types come from the constructor's signature, so decoding an attribute
means decoding a `MethodDefSig` or `MemberRefSig` first. Read one value at the wrong width and
every value after it is read from the wrong offset.

**An enum is a bare number, and the blob will not say how wide.** `[Audited(AuditLevel.Full)]`
stores one byte if `AuditLevel : byte` and eight if it is `: long`, and the blob says only "value
type" and which type. Assuming `int` reads most enums correctly and silently desynchronises
everything after a `byte`- or `long`-backed one — precisely why the mistake survives casual
testing. The width has to come from the enum's own definition: its single instance field, whose
signature *is* the underlying type.

That definition usually lives in **another assembly**, and getting there is where the real
difficulty was. Three things have to be right, and each fails silently on its own:

- **The reference map holds a `Module`, not an `Assembly`.** The descriptor calls the field
  `ManifestModuleReferencesMap` and the runtime's setter is named `StoreAssemblyRef` — but that
  setter takes an `Assembly*` and stores `value->GetModule()`, and the field is declared
  `LookupMap<PTR_Module>`. Reading it as an Assembly yields a structure that decodes without
  complaint and reports an empty name, so the error shows up as resolution that quietly never
  succeeds.
- **A reference assembly answers with a forwarder, not a definition.** `System.Runtime` and friends
  define almost nothing; their manifests are mostly `ExportedType` rows saying "that name really
  lives over there". Measured: *every* cross-assembly enum in an attribute went through a
  forwarder, so without this step none of them resolved.
- **A nested enum's reference carries only its short name.** `DebuggingModes` matches no `TypeDef`;
  `System.Diagnostics.DebuggableAttribute+DebuggingModes` does. The full name has to be rebuilt by
  walking the TypeRef's resolution scope.

Even with all three, the loader's maps are filled in **lazily**, so resolution through them
succeeds or fails depending on what the process happened to do first. The last resort avoids them
entirely: the descriptor publishes the MethodTable of `System.Object`, a MethodTable knows its
module, so **CoreLib is reachable from the contract descriptor alone** — and nearly every enum used
in an attribute is defined there.

Measured across CoreLib and five other loaded assemblies: **4,792 enum arguments, none guessed**,
every width matching `Enum.GetUnderlyingType` — 360 of them `long`-backed and 270 `byte`-backed, so
the hard cases are represented. Before the three fixes above, 85 arguments fell back to guessing
`int`; they all happened to *be* int-backed, which is exactly the kind of luck that hides a bug.
And a guess is never returned as though it were read: the whole blob must be consumed exactly, so a
wrong width leaves bytes over and becomes a `DecodeError`.

Going one step further recovers the member name too, from the enum's literal fields and their
`Constant` rows — so `Parts = 5` reads back as `AuditParts.Inputs | AuditParts.Timing`.

**A `typeof()` argument is a string, not a type.** It is the name the compiler wrote, referencing
the assemblies it compiled *against*:

```
typeof(Dictionary<string, int>)
  -> "System.Collections.Generic.Dictionary`2[[System.String, System.Runtime, ...]], System.Collections, ..."
```

Reflection resolves that and re-renders it under the runtime's own identities
(`System.Private.CoreLib`), so the two strings differ for the same argument. The literal string is
what was written; it resolves to the same type.

### The attributes that are not there

`[Serializable]`, `[StructLayout]`, `[DllImport]`, `[MethodImpl]`, `[NonSerialized]`, `[MarshalAs]`,
`[In]`, `[Out]` and the rest of ECMA-335 II.21 are **pseudo-custom attributes**. The compiler turns
them into bits in the defining table — `tdSerializable` in `TypeDef.Flags`, `MethodDef.ImplFlags`
for `[MethodImpl]` — and writes no row. Reflection synthesises them back on the way out, which is
why `GetCustomAttributesData()` reports them.

There is nothing in the `CustomAttribute` table to find, so they are absent from `CustomAttributes`
rather than faked, and a test asserts that difference so it stays a documented gap. One of them is
recoverable, because a MethodDesc's row can simply be read for it:

```csharp
method.ImplementationFlags      // the raw flags, matching GetMethodImplementationFlags()
method.PseudoCustomAttributes   // [MethodImpl(MethodImplOptions.NoInlining | ...)]
method.AllAttributes            // the rows plus the reconstructions
```

Reconstructions are kept apart from rows rather than mixed in, and each carries `IsSynthesised`, so
"was read" and "was rebuilt from flags" never blur. The rest stay out of reach: `[Serializable]` and
`[ComImport]` would need `TypeDef.Flags`, and `[StructLayout]` its arguments from the `ClassLayout`
table.

---

## Reading IL

`ClrMethodIl` reads a method's IL from either source:

```csharp
ClrMethodIl.Of(typeof(Order).GetMethod("Restock"));   // through reflection
ClrMethodIl.Of(table.FindMethod("Restock"));          // through the MethodDesc, out of the image
```

The two differ only in what a token becomes: a resolved `MemberInfo` on the reflection path, or a
`ClrIlToken` named from the module's metadata on the other. Everything else — the instruction walk,
the operands, the exception regions, the locals, the attributes — is the same.

```csharp
var il = ClrMethodIl.Of(restock);

il.Instructions;         // decoded: offset, opcode, operand, length
il.LocalVariables;       // slots: index, type, name, pinned, by-ref
il.ExceptionRegions;     // try/catch/filter/finally, from either source
il.Attributes;           // rows plus pseudo-custom attributes
il.DeclarationFlags;     // what public/static/virtual compile into
il.Metadata;             // the module metadata it was read through
il.MaxStackSize;
il.Bytes;

Console.WriteLine(il.Dump(IlDumpStyle.Auto));
```

```
// ClrSpectorConsole.Order::Restock
.maxstack 3
.locals init (
    [0] !!0 quantity,
    [1] int missing,
    [2] !!0 i,
    [3] bool,
    [4] string
)
IL_0000:  nop
IL_0001:  ldarg.0
IL_0002:  ldfld        ClrSpectorConsole.Order::Quantity
IL_0007:  constrained. !!0
IL_000d:  call         System.Numerics.INumberBase<!!0>::CreateChecked<int>
...
// Catch try IL_0058..IL_0078 handler IL_0078..IL_0083 catch System.InvalidOperationException
```

The opcode tables are built from the framework's own `OpCodes` at startup rather than written out,
so they cannot drift. On the reflection path, tokens resolve through the declaring module with the
type's and method's generic arguments supplied as context — without those, a token inside a generic
method will not resolve.

Operands on the MethodDesc path are `ClrIlToken` — named from metadata, not resolved to reflection
objects — and string literals come from the user string heap. A test asserts that no operand is a
`MemberInfo`, so the route stays reflection-free, and that the bytes and opcode stream are
identical to what reflection produces.

### The body in memory

`ClrMethodBodyImage` is the layer underneath:

```csharp
var body = restock.ReadIl();

body.IsFatFormat;              // one-byte header, or twelve
body.MaxStack;
body.LocalSignatureToken;      // names a blob; ClrModuleMetadata.LocalSignature decodes it
body.ExceptionRegions;         // read from the data sections that follow the code
body.Il;
```

The body begins with one of two headers: a **tiny** one-byte header when the method has no locals,
no handlers and a stack no deeper than eight, and a **fat** twelve-byte one otherwise, carrying the
real stack depth and the local signature token. Both are decoded, and a test checks each against a
method known to need it.

**The exception table is the only place a method's regions exist.** The runtime keeps no decoded
copy to ask for, so a body read from memory has to parse the data sections that follow the code: a
chain, each section announcing its own size and whether another follows, with clauses in two widths
(small — 16-bit offsets, 8-bit lengths — and fat, everything 32-bit). The last field of a clause is
a class token for a typed catch, a filter offset for a filter, and nothing at all for the other two
kinds.

```csharp
foreach (var region in il.ExceptionRegions)
    Console.WriteLine(region);
// Filter try IL_0001..IL_000f handler IL_0031..IL_0037 filter IL_000f
// Catch  try IL_0001..IL_000f handler IL_0037..IL_003d catch System.InvalidOperationException
// Finally try IL_0001..IL_003d handler IL_003d..IL_004c
```

`ClrIlExceptionRegion` is the shape both sources produce, so reflection's clauses and the ones read
from memory describe their handlers identically. Verified against reflection over ~2,100 methods:
kinds, offsets, lengths and filter offsets all match.

Locals are the same story. `ClrIlLocal` carries the index, whether the slot is pinned or by-ref, the
type from whichever source described it, and the name — see below. Verified against reflection over
~2,200 methods (723 with locals, 17 pinned slots): no shape mismatches.

### Replacing a method body

The bytes cannot be written back over the original: they live in a read-only mapped image, the
method is likely jitted already, and the supported route to new IL — a profiler's ReJIT — is not
reachable in-process. So a replacement body is **emitted as a method of its own** and the target's
dispatch slots point at it, which is the mechanism `MethodDetour` already uses and is reversible in
exactly the same way.

```csharp
// edit the method's own IL and put it back
var il = ClrMethodIl.Of(method);
var edited = il.Instructions.Where(i => i.OpCode != OpCodes.Ldfld).ToList();

using (MethodDetour.ReplaceIl(method, edited, il.Locals.Select(l => l.LocalType).ToList()))
{
    ...
}

// or write a body from scratch
using (MethodDetour.ReplaceBody(method, il =>
{
    il.Emit(OpCodes.Ldarg_1);
    il.Emit(OpCodes.Ldc_I4, 100);
    il.Emit(OpCodes.Mul);
    il.Emit(OpCodes.Ret);
}))
{
    calc.Scale(7);   // 700
}
```

Three things this gets right that are easy to get wrong:

- **Tokens are re-issued, not copied.** A metadata token only means something in its own module, so
  raw IL moved elsewhere would reference whatever member happened to share that number. The body is
  rebuilt through an `ILGenerator` with each operand handed over as the resolved `MemberInfo`. An
  operand the decoder could not resolve — including a `ClrIlToken`, which is a *name* and not a
  member — is **refused**, since emitting it would produce an invalid program discovered only at
  the call.
- **Short branches become long ones.** Re-emission moves instructions relative to each other, and a
  one-byte displacement that fitted before need not fit after.
- **The replacement is an instance method** whenever the target is one, so the receiver stays in
  argument slot zero and the hidden return buffer keeps its place behind it.

The strongest test of decoder and emitter together is a round trip: decode a body, emit it back
unchanged, and the method must behave identically — including one that reads `this.Factor` through
its own slot 0, and one with branches. A body with try/catch cannot be expressed as a flat
instruction list, so use `ReplaceBody` and the generator's `BeginExceptionBlock` for those.

---

## Local names, from the PDB

Local names are the one part of a method that is **nowhere in the runtime**. A body records its
locals' types and nothing else — ECMA-335 has no name column for a local — and the runtime never
loads a PDB. Only the compiler's debug output has them.

```csharp
var symbols = ClrModuleSymbols.AtImageBase(metadata.ImageBase);   // or .Of(module)

symbols?.Source;                                     // "embedded", or the path it was read from
symbols?.IsEmbedded;
symbols?.LocalNames((uint)method.MetadataToken);     // slot -> name
```

Reading it is automatic: `ClrMethodIl` fills in `ClrIlLocal.Name` when a PDB can be found, and
`DisplayName` falls back to `loc0` when it cannot. There are two places to look, and the mapped
image's debug directory names both:

- **embedded** (`<DebugType>embedded</DebugType>`): the portable PDB is inside the image itself,
  deflated, in a debug directory entry of type 17 — read straight out of mapped memory, like
  everything else here;
- **a file beside the assembly** (the default): a CodeView entry names its path and a GUID. That
  path is read from disk — the one place this library touches a file — and the PDB is accepted only
  when its own id matches the GUID the image recorded, so a stale PDB from an earlier build is
  rejected rather than believed.

A portable PDB is itself an ECMA-335 metadata container — same root, same streams, same table
format — so `MetadataImage` reads it with the schema extended by the PDB's own tables
(`Document`, `MethodDebugInformation`, `LocalScope`, `LocalVariable`, …). One subtlety makes it
work: a standalone PDB holds none of the module's rows but indexes into them, and restates their
row counts in its `#Pdb` stream purely so index widths can be computed. Those counts must widen an
index without contributing rows — conflate the two and every offset past the first index runs off
the end of the stream.

Slots that share a name are numbered apart (`i`, `i_1`). A PDB names a slot per lexical scope, and
two scopes that never overlap can each declare an `i` — as the arms of a switch over patterns do —
so a listing that called both `i` would print `i = i` for the copy between them. Compiler-generated
slots the PDB marks hidden keep their numbers.

---

## IL back to C#

The stack machine is the part of IL that is genuinely hard to read by eye, and undoing it is what
this always does: `ldloc.0; ldc.i4.1; add; stloc.0` reads as `loc0 = loc0 + 1;`. How much further
it goes is a choice.

```csharp
il.ToCSharp();                                   // ClrCSharpForm.Faithful, the default
il.ToCSharp(ClrCSharpForm.Structured);
il.DumpCSharp(IlDumpStyle.Auto, ClrCSharpForm.Structured);

var projection = ClrMethodCSharp.Of(method, ClrCSharpForm.Structured);
projection.Lines;        // tokens, offsets, and the IL each line came from
projection.IsExact;      // false if anything was not modelled
projection.Form;
```

**Faithful** leaves the control flow exactly as the IL has it. Branches become `goto`, every
statement is labelled with the offset it starts at, and nothing is inferred:

```
IL_0008:      if (loc1 < this.Quantity) goto IL_0014;  // ldloc.1; ldarg.0; ldfld Order::Quantity; blt.s IL_0014
IL_0011:      st1 = 1;                                 // ldc.i4.1
IL_0012:      goto IL_0015;                            // br.s IL_0015
IL_0014:      st1 = 0;                                 // ldc.i4.0
IL_0015:      loc0 = st0 + st1;                        // add; stloc.0
```

**Structured** goes on to undo the compiler's scaffolding — its temporaries, its conditional jumps,
its bottom-tested loops, its single-exit returns:

```
IL_0000:      int missing = 0;                         // nop; ldc.i4.0; stloc.0
              for (int i = 0; i < wanted; i++)
              {
IL_0007:          missing += i < this.Quantity ? 0 : 1;
              }
```

Every statement keeps the IL it came from as a trailing comment, in both forms, so the folding is
auditable against the listing above it — and so the `.ovf` and `.un` forms an operator cannot
express, the prefixes, and the instructions folded into an expression are all still named.

### What structuring does

Each pass is a pattern match with a proof obligation, and the proof is always the same shape: the
run of statements being rewritten has to be entered only where the rewrite says it is entered. So
each one counts the jumps that land on the labels it wants to remove, and refuses when the count is
not what the pattern requires. A pass that cannot prove its shape does nothing, and the gotos stand.

| Pass | Undoes |
|---|---|
| Redundant jumps | The `goto` at the end of nearly every block, which is what falling off the end already does. Jumps to it are redirected, never dropped. |
| Constant branches | `ldc.i4.1; brtrue` — the sequence-point scaffolding the compiler brackets a switch expression with. |
| Conditional expressions | The jump/assign/jump/assign diamond, back into `c ? a : b`, un-negating the condition and swapping the arms when the branch was written the other way round. |
| Single-use values | The compiler's temporaries and this projection's own spill slots, folded into the statement that reads them. |
| Return temporaries | The single-exit `locN = x; goto END; END: return locN`, back into a return per branch. |
| Compound assignment | `x = x + y` into `x += y`, and `x = x + 1` into `x++` — but only when the remainder is one whole expression, because `x = x - a - b` is not `x -= a - b`. |
| Loops | The jump-to-test/body/test/jump-back shape into `while` or `for`, widest first; the loop variable is declared in the header when the loop is the only place it is used, and jumps to the test or the exit become `continue` and `break`. |
| Conditionals | A forward conditional jump over a run of statements into an `if`. |
| Single-entry tails | A block exactly one jump reaches, that nothing falls into, and that ends by leaving the method — moved to where that jump is. This is what a switch expression needs. |
| Declarations | A local's declaration moved onto the statement that first assigns it. |

Plus renderings that are about what the source wrote rather than what the IL says: `op_LessThan(a, b)`
as `a < b`, `get_Current()` as `.Current`, `get_Item(i)` as `[i]`, `String.Concat(a, b)` as
`a + b`, `(&loc0).ToString()` as `loc0.ToString()`, a struct's in-place constructor as an
assignment, `!!0` as `T`, and `ldc.i4.1` stored into a `bool` slot as `true`.

### What it will not do

- **Reconstruct syntax.** A switch expression comes back as the chain of tests it compiles to, not
  as a `switch` expression: inferring patterns back out of a decision tree is guessing.
- **Duplicate code.** A block with two predecessors stays where it is.
- **Model a jump table.** A `switch` instruction and its cases are printed as they are.
- **Fold a variable the source named.** A compiler temporary is folded into the expression that
  reads it; a local the author declared is not.
- **Invent names.** A local is called what the PDB says, or `loc0`.
- **Compile.** It is a reading aid. Where something is not modelled it says so — as a comment, and
  as `IsExact == false` — rather than quietly producing a statement that is not what the method does.

### Modern C#, there and back

The tour's `modern C#` section runs a sample built out of the constructs that have no IL equivalent
at all, which is the interesting case:

| Source | What comes back |
|---|---|
| `where T : INumber<T>`, `new T()` | The constraint on the signature, and `Activator.CreateInstance<T>()` — which is what `new T()` compiles to. |
| `n switch { < 0 => …, 0 => …, _ => … }` | The decision tree it is, as a chain of `if`s with the arms lifted into them. |
| A .NET 11 `union` | `shape.Value` and one `isinst` per case — a union is a type with an `object` holding whichever case it is. |
| `catch (X e) when (…)` | A `filter` block of its own with the condition inside it, then the `catch` it guards. A `when` clause is not part of the catch in IL. |
| Type, property, relational and combinator patterns | The tests and casts they compile to: `(int)value`, `s.Length == 0`, `.Radius > 10`. |
| List patterns with a slice | Length checks, indexing, and `RuntimeHelpers.GetSubArray<int>(values, new Range(…))`. |
| `foreach` | The enumerator loop inside a try/finally that it is. |

### Colour

Both dumps and the IL listing share one palette and one style, so an operand named in the IL and
the same operand named in the C# are the same colour:

```csharp
il.Dump(IlDumpStyle.Auto);                                  // colour if the output looks like a terminal
il.DumpCSharp(IlDumpStyle.Ansi, ClrCSharpForm.Structured);  // always
il.DumpCSharp(IlDumpStyle.Plain);                           // never
```

`Auto` honours `NO_COLOR` and treats a redirected stream as not a terminal, so the same call is
right for a console and for a log file. Things are coloured by what they *do* rather than by
syntax, since control flow, calls and the names of things outside the method are the parts worth
finding by eye. A test asserts that stripping the escapes from a coloured dump gives back the plain
one exactly, so the two renderings cannot drift apart — and `ClrCSharpTokenKind` is what a
projected token *is*, so a consumer can render it some other way entirely.

---

## Code, dispatch and detours

### The problem

To mock a dependency in a test you normally extract an interface, even when the seam exists purely
for the test. `MethodDetour` provides that seam without the interface: it redirects calls to a
concrete method so they run a stand-in instead, and restores the original when disposed.

| Method kind | Dispatched through | Supported |
|---|---|---|
| `static` | precode | yes |
| non-virtual instance | precode | yes |
| `virtual` | vtable **and** precode | yes — both are patched |
| `override` | vtable | yes, including through a base-typed reference |
| sealed `override` via abstract base | vtable | yes |
| `abstract` declaration | nothing — no implementation | refused, with an explanation |
| reached by **interface** dispatch | interface stub cache | refused by default — [it cannot be undone](#interface-dispatch-cannot-be-undone) |

### What does not work: patching the code

The obvious approach is to overwrite the target's machine code with a jump to the replacement. It
does not work, and it fails in a way that looks like success:

```
from=0x7923a5d19908 to=0x7923a5d19920
before:       real:x
mprotect rc=0
orig bytes:   ff 25 fa 3f 00 00 4c 8b 15 fb 3f 00
after write:  48 b8 08 99 27 a3 3e 70 00 00 ff e0   ← the write landed
after patch:  real:x                                ← but nothing changed
```

The reason is visible in the original bytes: `ff 25 …` is `jmp qword [rip+disp32]`, so the entry
point is **not the method body** — it is a stub. **The method body never needs to be found at all.**

### What a precode is

A jitted method is reached through a *precode* — a small stub standing in for the method's entry
point, forwarding to wherever the real code currently lives. It exists because the real code moves:
the method may not be jitted yet, and tiering may replace it later. On x64 that stub is a single
rip-relative jump through one pointer-sized slot:

```
entry point ──▶  ff 25 fa 3f 00 00        jmp qword [rip+0x3ffa]
                                                      │
                                          ┌───────────┘
                                          ▼
                              dispatch slot: ──▶ current real code
```

That slot is the interception point for every **non-virtual** call. The runtime publishes its own
precode constants, so nothing is guessed:

```csharp
var machine = PrecodeMachineInfo.Current;

machine.FixupPrecodeType     // 2
machine.StubPrecodeType      // 3
machine.FixupCodeOffset      // 6      - matches the rip-relative jump length
machine.StubPrecodeSize      // 24
machine.StubCodePageSize     // 16384  - the stub-to-slot distance
```

`fixupCodeOffset=6` independently confirms the 6-byte jump length, and `codePageSize=16384`
explains where the slot lands: the dispatch slots live on a writable data page exactly one code
page away from the executable stub page. That relationship is asserted in the tests.

The descriptor also publishes the byte pattern the runtime built its precodes from, plus a mask of
the positions that vary, so `precode.IsFixupPrecode` compares against the template from the same
build rather than testing for `ff 25`:

```
FixupBytes        = ff 25 fa 3f 00 00 4c 8b 15 fb 3f 00 00 ff 25 fd 3f 00 00 00 00 00 00 00
FixupIgnoredBytes = 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 01 01 01 01 01
actual entry      = ff 25 fa 3f 00 00 4c 8b 15 fb 3f 00 00 ff 25 fd 3f 00 00 90 66 66 66 66
```

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

So redirecting the precode alone leaves every virtual call running the original, **silently**. That
was measured before it was fixed: `virtual`, `override`, sealed `override` and interface-implementing
methods all reported "not redirected" while non-virtual and static worked. `MethodDetour` therefore
patches **both** paths whenever both exist — patching the vtable alone would miss a virtual method
invoked non-virtually (`base.M()`, or a call the JIT devirtualized).

### Where the vtable lives, and why it is chunked

The vtable is **not** one contiguous array. It is an array of *chunk pointers* beginning
immediately after the MethodTable's fixed fields, with **8 slots per chunk**:

```csharp
chunkPointer = *(IntPtr*)(methodTable + MethodTable.Size + (slot / 8) * IntPtr.Size);
slotAddress  = chunkPointer + (slot % 8) * IntPtr.Size;
```

**Chunks are not adjacent, and they are shared.** A derived type that overrides nothing in a chunk
reuses its base type's chunk, which can sit at a *lower* address than the MethodTable itself —
measured on a subclass overriding only a late slot:

```
Sub MethodTable 0x7dd8e6cc3600   numVirtuals=16
  chunk 0 -> 0x7dd8e6cc3518   (MethodTable-232)   ← shared with the base type
  chunk 1 -> 0x7dd8e6cc3650   (MethodTable+80)

  name  slot   contiguous model      chunked model
  V0    4      MATCH                 MATCH
  V9    13     MATCH                 MATCH
  V11   15     wrong address         MATCH        ← the overridden slot
```

The two models agree only when chunks happen to be laid out adjacently, which is why a naive
implementation can pass casual testing and still be wrong. The offset of the chunk-pointer array is
the descriptor-published `MethodTable` size; the 8-slots-per-chunk figure is a CoreCLR compile-time
constant the descriptor does *not* publish, so it is verified against types with more than one
chunk and exposed as `MethodVtable.SlotsPerVtableChunk`.

Chunk sharing has a visible consequence: a vtable patch applies to the **declaring type**, and
subclasses that inherit the slot unchanged are affected too. Subclasses that *override* have their
own slot and are unaffected — asserted by `RedirectingTheBaseLeavesAnOverridingSubclassAlone`.

### Performing the swap

Each redirect is one pointer-sized store, applied to every dispatch path the method has. The
method's machine code is never modified:

```csharp
CodeProtection.MakeWritable(address, IntPtr.Size);

var original = *(IntPtr*)address;      // remember
*(IntPtr*)address = value;             // redirect
```

The replacement's *own* entry point is stored — its precode, not raw code — so the chain becomes
`target slot → replacement precode → replacement code`, which is more robust than pointing at a
code address directly. Restoring replays every patch in reverse, and `Dispose` is idempotent;
because it is an `IDisposable`, a `using` block restores on every path out, including an exception.

For a non-virtual method there is one slot and every caller goes through it, so the redirect is not
specific to how the call is written. All three of these are asserted:

```csharp
service.GetPrice("x");                         // direct call      → proxy
viaDelegate(service, "x");                     // open delegate    → proxy
method.Invoke(service, new object[] { "x" });  // reflection       → proxy
```

### The hidden return buffer

Matching parameter lists are not enough, because they are not the whole frame. Arguments are passed
in this order:

```
[this] [return buffer] [generics context | varargs cookie] [user arguments]*
```

A return value too large for a register is written through a hidden pointer the caller supplies,
and on x64 that pointer is an ordinary argument sitting **after** `this`. So an instance method
returning a `decimal` receives `(this, returnBuffer, sku)`, while a static stand-in taking the
instance first receives `(returnBuffer, instance, sku)`. Everything shifts by one: the instance is
reinterpreted as a buffer, and the return value is written over the target object.

```
[retbuf] redirected: result=0 (want 42)                 <- caller's buffer never written
[retbuf] marker=0x2a (want 0x1122334455667788)          <- 42 written INTO the target object
Fatal error. Internal CLR error.                        <- next GC dies on the trampled object
```

arm64 escapes this: it has a dedicated return-buffer register (`x8`) outside the argument sequence.

The fix is a generated **thunk**. Two pairings cannot occupy a dispatch slot as they are:

| Pairing | Why it needs an adapter |
|---|---|
| `AbiShim` | A static stand-in for an instance method whose return value travels in a hidden buffer — the shift above. |
| `ReceiverShift` | An **instance** stand-in: a proxy object, whose own receiver has to come from somewhere. A slot holds a code address and nothing else. |

The adapter is emitted as **IL and compiled by the JIT**, never as hand-written machine code, so
return buffers, floating-point registers, spilling and x64-versus-arm64 differences are handled by
something that already knows the answer. It is emitted as an **instance** method whenever the
target is one, so the adapter's receiver occupies the same slot as the target's — which is also why
`DynamicMethod` cannot serve here: it is always static, precisely the broken shape. `TypeBuilder`
also yields a real `MethodHandle` and non-collectible code, so no private reflection is needed and
the adapter cannot be freed while a slot still points at it.

`detour.Pairing`, `detour.UsesThunk` and `detour.ThunkEntryPoint` report which path was taken.

### Interface dispatch cannot be undone

One case is genuinely not supportable, and it is the sharpest edge here. Interface dispatch does
not read the class vtable directly — it resolves through a dispatch stub and **caches the result**.
That cache is not reverted on dispose, so a call made through an interface reference while
redirected leaks the proxy *permanently and process-wide*:

```
concrete before : real
interface during: PROXY
concrete after  : real       ← the vtable and precode were restored correctly
interface after : PROXY      ← but interface dispatch still resolves to the proxy

fresh instance, after restore:
interface on new instance: PROXY     ← even objects created later
```

In a test suite that is the worst possible failure: silent contamination of every later test. So
`MethodDetour` **refuses** a method that implements an interface member, with an explanation, and
the guard can be lifted knowingly with `allowInterfaceDispatch: true` when the interface path is
genuinely never exercised. If a type has an interface, mocking through the interface is the better
tool.

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

When the stand-in needs state of its own, write it as an ordinary **instance** method whose
parameters match what the target receives, and pass the object alongside it — a generated thunk
supplies the receiver:

```csharp
var proxy = new PriceServiceProxy();

using (MethodDetour.Redirect(
           typeof(PriceService), nameof(PriceService.GetPrice),
           proxy, nameof(PriceServiceProxy.GetPrice)))
{
    await Assert.That(new PriceService().GetPrice("abc")).IsEqualTo(142m);
}

await Assert.That(proxy.Seen).IsEquivalentTo(new[] { "abc" });
```

A delegate carries its receiver, so a method group or a closure works too:

```csharp
using (MethodDetour.Redirect(
           typeof(PriceService).GetMethod(nameof(PriceService.GetPrice)),
           (Func<PriceService, string, decimal>)((instance, sku) => captured)))
{
    ...
}
```

The proxy is bound to the redirect rather than baked into the adapter, so disposing releases it.
The adapter code itself is emitted once per distinct pairing and is never reclaimed.

A mismatched replacement would corrupt the stack, so pairings are validated up front and refused
with `MethodDetourException` rather than producing undefined behaviour at the call. Return types
must match, and so must the **effective** parameter lists — where "effective" accounts for an
instance method receiving its declaring type as a leading `this` (by reference, when that type is a
struct). Generic methods, methods on generic types, varargs methods and methods on value types are
refused outright: each needs a hidden argument, or has an entry point, that a redirect cannot
honour. A refused redirect leaves the target callable — also asserted.

### Inspecting dispatch without redirecting anything

```csharp
var precode = MethodPrecode.Of(typeof(Svc).GetMethod("Virt"));

precode.EntryPoint        // the stable entry point (the stub, not the body)
precode.HexBytes          // "ff 25 fa 3f 00 00 4c 8b 15 fb 3f 00 00 ff 25 fd"
precode.IsRipRelativeJump
precode.Disassembly       // "jmp qword [rip+16378]"
precode.DispatchSlot      // the one slot a redirect writes to
precode.DispatchTarget    // where it currently points

MethodVtable.FindSlotNumber(method);   // vtable slot index, or -1
MethodVtable.FindSlot(method);         // the slot's address, or IntPtr.Zero
MethodVtable.SlotsPerVtableChunk;      // 8
```

```
Svc.Virt entryPoint=0x74260af7bb10 [ff 25 fa 3f 00 00 …] jmp qword [rip+16378]
         slot=0x74260af7fb10 -> 0x74260a9d9300
```

A live detour also reports what it did: `detour.PatchedTargets` (`Precode`, `Vtable`, or both),
`detour.VtableSlot`, `detour.Precode`, `detour.IsActive`.

### An address back to its method

`ClrCodeMap` goes the opposite way to everything else here: from a bare code address to the method
it belongs to. Two structures do the work — a five-level range section map that partitions the
address space by successive bytes of the address, then a nibble map that finds the individual
method inside a code heap. The code header sits one pointer behind the method's code and names the
MethodDesc.

```csharp
var block = ClrCodeMap.Current.Find(someAddress);
// 0x7ffa29988444 Sample.Add+0x4 (start=0x7ffa29988440)

block.Kind;                 // Jitted, Stub, ...
block.ResolveMethod();
block.OffsetIntoMethod;

ClrCodeMap.Current.FindMethod(returnAddress);   // -> MethodBase
```

An address anywhere *inside* a method resolves to it, not just its entry point, which is what makes
it useful on a return address, an exception frame or a continuation's resume point. Precodes and
dispatch stubs report as `Stub`, ready-to-run and interpreter ranges report as themselves, and an
address that is not code returns null.

### What tiering actually did

`IsEligibleForTieredCompilation` says the runtime *may* recompile a method. `CodeVersions` says
what it has done — and with the code map, you can watch it happen:

```
first compile: slot -> 0x7ffa2999b410  Hot.Work+0x0
after work:    slot -> 0x7ffa2999fcf0  Hot.Work+0x0        <- the slot was rewritten
   versions=2
      tier=Tier1             code=0x7ffa2999fd20
      tier=Tier0Instrumented code=0x7ffa2999fcf0
```

That rewrite is exactly what silently drops a detour, which is why the guard exists —
`MethodDesc.Flags3AndTokenRemainder` carries the eligibility bit, so "will the runtime recompile
this and undo my redirect?" can be *asked* rather than assumed from build configuration. Measured:

| | methods flagged eligible |
|---|---|
| `<TieredCompilation>false</TieredCompilation>` | 0 of 11,621 sampled |
| tiering enabled | 10,424 of 11,621 sampled |

So the refusal fires precisely when someone forgot the setting, and never otherwise. It can be
waived per redirect with `allowTieredCompilation: true`.

### Limits you must know

These are properties of the technique, not gaps in the implementation.

| Limit | Why | What to do |
|---|---|---|
| **Inlined calls cannot be intercepted** | If the JIT inlined the callee, no call happens at all. | Mark redirectable methods `[MethodImpl(MethodImplOptions.NoInlining)]`. |
| **Tiered compilation rewrites the same slot** | Promoting a method to optimised code updates the dispatch slot. | Refused automatically; set `<TieredCompilation>false</TieredCompilation>` or pass `allowTieredCompilation: true`. |
| **The slot is process-wide** | Two tests redirecting the same method concurrently each undo the other. | Serialize them. With TUnit, `[NotInParallel]`. |
| **Interface dispatch leaks** | The resolved target is cached and the cache is not reverted. | Refused by default. Mock through the interface instead. |
| **A vtable patch is per declaring type** | Subclasses inheriting the slot are affected; overriding subclasses are not. | Redirect the type whose behaviour you mean to replace. |
| **x64 verified only** | The `jmp` decoding is x64-specific. | arm64 is unverified. |
| **Not for production** | Mutating runtime dispatch state is not thread-safe against concurrent calls to the target. | Use it in tests. |

The parallelism limit is not theoretical — it surfaced as a real test failure while building this,
with two tests fighting over one slot.

---

## The runtime's other structures

### Async continuations

.NET 11's runtime async replaces the compiler's state-machine struct with a heap `Continuation` per
suspension, so a suspended `await` chain is a linked list on the heap that can be walked:

```csharp
var pending = AwaitsTheInnerCall();      // still suspended

// A suspended method parks its continuation in the awaited task's own continuation slot,
// which reaches the chain without walking the heap.
var slot = typeof(Task).GetField("m_continuationObject", BindingFlags.Instance | BindingFlags.NonPublic);
var continuation = ClrContinuation.Of(slot.GetValue(gate.Task));

Console.WriteLine($"{continuation.Chain().Count} links");
Console.WriteLine(continuation.Dump());
```

```
4 links
   resume state=0 at <no resume ip>
   resume state=0 at 0x7ffa29993bbc AwaitChain.AwaitsTheGate+0xec
   resume state=0 at 0x7ffa2999399f AwaitChain.AwaitsTheMiddleCall+0x9f
   resume state=0 at 0x7ffa2999315f AwaitChain.AwaitsTheInnerCall+0x9f
```

Each link's resume point resolves through `ClrCodeMap` to the method it will return to, so a
suspended chain says what the task is actually waiting to do — something the compiler-generated
state machines never made visible. The chain is only reachable **while the awaited task is
pending**: completing it runs the continuations and unlinks them.

The layout is checked twice over — the contract's offsets against the managed
`System.Runtime.CompilerServices.Continuation` type's own field offsets, and then against a live
chain.

### Managed threads

The runtime's own ThreadStore, which is both complete and managed-only — unlike `Process.Threads`,
which lists OS threads and cannot tell you which are managed:

```csharp
foreach (var thread in ClrThreadStore.Read().Threads)
    Console.WriteLine($"{thread.ManagedThreadId} os={thread.OsThreadId} {thread.State} " +
                      $"coop={thread.IsInCooperativeMode} stack={thread.StackLimit:x}-{thread.StackBase:x}");
```

```
thread @0x26942521980 managedId=1 osId=16000 coop=False stack=1536KB
thread @0x2694430e690 managedId=2 osId=33216 coop=True  stack=1536KB
```

`coop` is the one you cannot get otherwise: whether the thread is running managed code, so the GC
must suspend it rather than ignore it.

### Where a thread actually is

**There is no current instruction pointer to read.** A running thread's IP is in its registers and
on its own stack; the runtime caches it nowhere, so there is no field to find. The only routes to
it are to suspend the thread and call `GetThreadContext`, or to be that thread.

What the runtime *does* record is the explicit **frame chain**. Every time a thread crosses a
boundary jitted code cannot describe by itself — a P/Invoke, a stub, a hijack for suspension, an
exception dispatch — it pushes a `Frame` holding what is needed to get back across it: a return
address, a MethodDesc, or a saved register context.

```csharp
foreach (var frame in thread.Frames)
    Console.WriteLine(frame);          // InlinedCallFrame ip=0x… in Program::SleepWindows+0x80

thread.InnermostManagedFrame?.Method;  // the ClrMethodDescription
thread.InnermostManagedFrame?.CodeBlock.OffsetIntoMethod;
```

```
thread 2   coop=True
  where    no frames - running managed code, so nothing records an ip
thread 3   coop=False
  frame    InlinedCallFrame ip=0x7ffa29d84e60 in ClrSpectorConsole.Program::SleepWindows+0x80
  frame    DebuggerU2MCatchHandlerFrame
  its ip   0x7ffa29d84e60 is ClrSpectorConsole.Program::SleepWindows+0x80
```

Both answers are honest, and the empty one is the point: a thread running managed code straight
through has `FRAME_TOP` and there is genuinely nothing recorded. This is not a stack walk — it is
the set of boundaries the thread has crossed.

Three things about frames are not guessable:

- **A frame no longer identifies itself by vtable.** The first pointer-sized slot is a small
  `FrameIdentifier` enum value, and the descriptor publishes one `<Name>FrameIdentifier` global
  per kind. Looking for a vtable pointer there finds a small integer like `0x12` and matches
  nothing.
- **Those globals are literal numbers, not addresses.** Read as addresses every one of them comes
  back zero — which looks exactly like the descriptor not publishing them at all.
- **`FRAME_TOP` is `~0`, not null.** A walk that stopped only on null would dereference it.

The kind's name is then the name of the descriptor type describing its fields, so which offsets to
read comes from the descriptor rather than from a table here. An identifier the descriptor does not
name reads *nothing* off the frame: the offsets depend entirely on the kind, so a half-written
identifier — a real possibility, since a chain is mutated by the thread that owns it — stays
harmless instead of convincing.

Reading another thread's chain also has to assume every pointer on it is bad. The chain is mutated
by the thread that owns it, so a snapshot can catch a frame mid-push: measured under load, other
threads' chains produced identifiers like `7821424755623103304` alongside the real ones. An
unrecognised kind therefore has *nothing* read off it — the offsets depend entirely on the kind —
and every pointer that is followed (a MethodDesc, its MethodTable, that table's EEClass and Module)
is alignment-checked and page-probed first. That is not defensive habit: **an access violation in
.NET is a fatal error, not a catchable exception**, so a `try`/`catch` around the read cannot save
the process. Chasing this down found three separate paths that would happily take the process out
on a stale pointer.

One limitation, stated because the sample shows it: an address in **ReadyToRun** code is placed in
a range but not named. A jitted method's code header carries its MethodDesc; precompiled code does
not, and naming a method there needs that image's own function table — a different lookup than the
code map does. A wait made through CoreLib lands there, which is why the sample parks its thread in
a P/Invoke declared in the sample assembly, whose marshalling stub is jitted.

### An exception's captured frames

`Exception.StackTrace` gives you a formatted string. The underlying data is an array of
`(instruction pointer, MethodDesc)` pairs on the exception object:

```csharp
foreach (var frame in ClrExceptionTrace.Of(caught))
    Console.WriteLine(ClrCodeMap.Current.Find(frame.InstructionPointer));

Console.WriteLine(ClrExceptionTrace.Dump(caught));
```

```
0x7ffa29971be1 Program.Deep+0x31
0x7ffa29971b89 Program.Middle+0x9
0x7ffa29971b59 Program.Outer+0x9
0x7ffa2997188a Program.Main+0x2a
```

Each frame is a `MethodBase` you can act on rather than text to parse, and it works on an exception
that was never thrown far enough to have its string built.

### Modules, assemblies and tokens without types

A module keeps a lookup table from each metadata token to the runtime structure for it, so a token
can reach a MethodTable with no `Type` ever existing — the direction reflection will not go:

```csharp
var module = ClrModule.Of(typeof(Order));

module.SimpleName;
module.Base;                                 // where the image is mapped
module.TypeDefToMethodTable(0x02000002);     // -> MethodTable
module.MethodDefToMethodDesc(methodToken);   // -> MethodDesc

ClrAssembly.At(module.Assembly);
ClrLoaderAllocator.At(module.LoaderAllocator);   // the heaps precodes are allocated from
```

Zero means "not loaded yet" rather than "no such type" — the runtime builds a MethodTable on first
use.

---

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
        Console.WriteLine("   " + segment);
}

foreach (var instance in heap.EnumerateObjects(scope))
    Console.WriteLine(instance);              // object @0x… size=40 mt=0x…

scope.ThrowIfInvalidated();
```

And for one object you already hold:

```csharp
using var scope = GcWalkScope.Enter();
var entry = ClrHeapObject.Of(myObject, scope);

Console.WriteLine($"{entry.Size} bytes, gen {entry.Generation}, {entry.MethodTable.Name}");
Console.WriteLine($"on the LOH: {entry.Segment?.IsLargeObjectHeap}");
```

```
Sample     addr=0x1928486a310 size=32     count=0      gen=0 loh=False  AbiProbe.Sample
String     addr=0x1b1942b0dc8 size=40     count=8      gen=2 loh=False  System.String
Int32[]    addr=0x1928486a330 size=64     count=10     gen=0 loh=False  System.Int32[]
Byte[]     addr=0x19284c00048 size=100024 count=100000 gen=3 loh=True   System.Byte[]
```

The size is the part reflection cannot give you: it is what a heap walk advances by, including the
header, an array or string's component count, and the GC's alignment rules. **An object's address
is only true until the GC moves it** — nothing here pins anything, so take a `GcWalkScope` for
anything longer than a single read. `Of` also re-checks the MethodTable it decoded against the
object's actual type, so a move is reported rather than returned as nonsense.

It is read-only. Nothing here writes to a GC structure, and nothing should: mutating them from
inside the process being collected corrupts the heap. `GC.TryStartNoGCRegion` is the only supported
lever over collection, and `GcWalkScope` uses it for exactly that.

### The GC has its own, unexported descriptor

The GC heap layouts are **not** in `DotNetRuntimeContractDescriptor`. The GC is pluggable, so its
descriptor cannot be a fixed export — and it is not reachable from any export, nor from any global
in the runtime descriptor. What the runtime does instead is embed one descriptor per GC flavour it
was built with, and leave them in its data section. On .NET 11 x64 there are three `DNCCDAC`
headers a few kilobytes apart:

| `.data` RVA | Contracts | `GCIdentifiers` |
|---|---|---|
| `0x45d940` | `GC: c1` (10 types, 45 globals) | `workstation, regions, background,` |
| `0x45d968` | `GC: c1` (11 types, 29 globals) | `server, regions, background, dynamic_heap` |
| `0x460020` | the runtime contracts | — (this one is the export) |

Only one of them describes the GC actually running. `GcContractDescriptor` finds them by scanning
the runtime module's readable regions for the header magic, and picks the one whose `GCIdentifiers`
matches `GCSettings.IsServerGC`. An ambiguous or empty result fails loudly, because picking the
wrong one would not crash — it would report a plausible but wrong heap. The scan is driven by the
operating system's own memory map rather than a fixed window, because an access violation in a
process reading its own internals cannot be caught: it takes the process down.

**.NET 10 publishes no GC descriptor at all**, which is why heap walking needs .NET 11. Under a
standalone GC (`DOTNET_GCName`, `clrgc.dll`) the descriptors live in that module instead.

### Generations, segments and regions

There are **five** generations, not three: gen0–gen2 are the small object heap, and the two beyond
`MaxGeneration` are the large and pinned object heaps. The count comes from the descriptor's
`TotalGenerationCount`.

Each generation's `StartSegment` heads a `Next` chain of `HeapSegment`. The four bounds nest —
`Mem <= Allocated <= Committed <= Reserved` — and objects live in `[Mem, Allocated)`. Two kinds of
segment break the obvious rules:

- **Frozen segments** (`IsReadOnly`) hold objects baked into a ReadyToRun image, literal strings and
  the like. They are mapped from the image, so they sit *outside*
  `GCLowestAddress`/`GCHighestAddress` and the range check does not apply.
- **The ephemeral segment** (`IsEphemeral`) reports `Allocated == Mem` on a live heap, because the
  GC only writes that field back when it collects. Its real end is the GC's `alloc_allocated`
  counter.

Workstation GC keeps one heap and the descriptor's globals point straight at its fields, so the
generation table is the array at `GCHeapGenerationTable`'s own address — `Globals.Address`, not
`Dereference`. `MaxGeneration` is the opposite trap: an int-sized variable, read *at* the symbol's
address.

### Sizing an object

An object's MethodTable pointer needs the GC's mark and pin bits cleared with
`ObjectToMethodTableUnmask` — an unmasked read gives an address that is wrong for part of every
collection. The size is then `BaseSize`, plus `ComponentCount * ComponentSize` for an array or
string, rounded up to pointer alignment with a three-pointer minimum.

`BaseSize` is measured from the object *header*, not from the MethodTable pointer, which is why a
class with three `long` fields comes out at 40 bytes rather than 32. The walk advances from one
object's MethodTable pointer by exactly this size and lands on the next one's — the same arithmetic
the GC does.

### The gaps that make a naive walk lie

This is the part that matters. The GC hands **each thread** its own zeroed allocation buffer, and
only the part a thread has used holds objects; the rest sits as a run of zeroes in the middle of the
range the walk covers. Both obvious responses are wrong:

- **Stop at the first gap** and the walk is safe but reports about **13%** of the heap.
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
filler. Those bytes are zero too, so a skip that stops at `Limit` exactly lands back in them and the
walk gives up 24 bytes later.

`Thread` has no published size, so the readability probe covers exactly the fields being read. Using
the absent size makes every probe zero-length, which reads as unreadable — and then no buffers are
collected at all, silently, and you are back to the 13% case.

### Workstation and server GC

The two flavours keep the same structures in different places, and the descriptor publishes a
different set of globals for each — so this is genuinely two routes to the generation table:

| | Workstation | Server |
|---|---|---|
| Heaps | one | one per core |
| Generation table | `GCHeapGenerationTable` **is** the table | inline in each `gc_heap`, at `GCHeap.GenerationTable` |
| Ephemeral segment, alloc pointer | `GCHeapEphemeralHeapSegment`, `GCHeapAllocAllocated` globals | fields of each `gc_heap` |
| Heap list | — | `Heaps` (a `gc_heap**`) and `NumHeaps` |

Generations are flattened across heaps so a walk covers the whole process either way. `HeapIndex`
says which heap a generation came from, and `HeapCount` / `GenerationsOfHeap(i)` separate them
again:

```
gc heap "server, regions, background, dynamic_heap" heaps=4 generations=20 segments=37
  heap0 @0x1d561238d50 gens=5 segments=10 live=468720
  heap1 @0x1d56123dc30 gens=5 segments=9  live=0
walked 4636 objects, 460688 bytes across 4 heap(s)
```

Cross-check: the same program under workstation GC walks 4,614 objects and 459,640 bytes, so the
server path is finding the same heap rather than one core's share of it.

### Reading a heap that is moving

Walking a live heap from inside it is not the same as walking a suspended target. The honest limits:

- `GcWalkScope` commits enough memory up front that no collection is needed, so nothing moves. The
  budget can still be exhausted, at which point a collection happens anyway — so the collection
  counts are compared and `CollectionOccurred` / `ThrowIfInvalidated` say whether the results can be
  trusted. `EnumerateObjects(scope)` also abandons the walk as soon as it notices one.
- A region can be **decommitted** underneath the walk when a collection runs, and reading a
  decommitted page is fatal to the process. Every page is checked before it is read, and the answer
  memoised, so it costs one system call per four kilobytes walked rather than one per object.
- The ephemeral segment's contents are genuinely in motion. A boundary the walk cannot make sense of
  there ends the segment; in a settled segment the same thing is a hard error, because there it
  really would mean the layout was misread.
- Don't allocate heavily while enumerating. The walk is a snapshot read, and a consumer that
  allocates per object both eats the no-GC budget and pushes the buffers the walk steps around.

### What is not covered

- **Some regions do not decode.** After repeated forced collections a minority of regions hold data
  the bump walk cannot follow, and the walk raises rather than fabricating objects. Most likely
  regions on a free list or not yet swept.
- **Roots.** This walks the heap by address, not by reachability. Nothing here enumerates roots or
  says whether an object is live.

---

## Verification

```bash
cd src/ClrSpectorTests && dotnet run -c Debug
```

**359 tests.** The interesting ones are not "does not crash" but the cross-checks, because the
failure mode of everything here is a plausible wrong answer. The suite is built around facts
obtainable **independently** of the decoder:

| Check | Against |
|---|---|
| Parent MethodTable identity, `EEClass` round-trip | `typeof(object)`, and the back-pointer returning to itself |
| Field and method counts, category flags | Reflection's `IsValueType`/`IsInterface`/`IsArray` |
| Reconstructed method tokens | `MethodInfo.MetadataToken` |
| Descriptor globals | `typeof(object\|string\|object[]).TypeHandle.Value` |
| Field offsets | Written and read back through the offset the runtime reported |
| MethodDesc chunk walk | Each step against the MethodDesc's own `ChunkIndex`, over ~43,000 MethodDescs across CoreLib |
| Attribute decoding | `GetCustomAttributesData`; all 30,034 CoreLib rows decode, 4,792 enum arguments with no guesses |
| Exception regions read from memory | Reflection's own clauses, over ~2,100 methods |
| Local signatures read from memory | Reflection's locals, over ~2,200 methods |
| Continuation layout | The managed `Continuation` type's field offsets, and a live suspended chain |
| IL decode | Instruction lengths summing to the body size; every branch target on an instruction boundary |
| IL round trip | Decode a body, emit it back unchanged, and the method behaves identically |
| Entry points, precode, vtable slot from a MethodDesc | Bit-identical to reflection's |
| The C# projection | Over 2,051 CoreLib methods plus the samples, in **both forms** |

That last row is the one that catches real bugs. Four invariants hold over every method walked:
every `goto` lands on a label that is printed, the braces balance, every instruction is modelled
(`IsExact`), and **the set of instructions each statement is attributed with is identical between
the faithful and structured forms** — which is what proves structuring rearranges what a statement
*says* and never what the method *does*. Every rewriting pass has been caught by it at least once:
a jump whose label had been deleted, a folded `nop` stealing a branch target's label, a loop
initialiser losing its IL.

The GC flavour is deliberately *not* pinned: both are supported, so the suite runs as-is under
either. To exercise the server path:

```bash
DOTNET_gcServer=1 DOTNET_GCDynamicAdaptationMode=0 DOTNET_GCHeapCount=4 \
  dotnet run --project ClrSpectorTests/ClrSpectorTests.csproj
```

Verified green at 1, 4 and 8 heaps as well as workstation.

---

## Platform support and version traps

Targets **.NET 11**. Type decoding and the GC heap walk are verified on
**.NET 11.0.0-preview.7.26381.103, win-x64**. `Architecture` and `OperatingSystem` are descriptor
globals, so the inspector is portable in principle, but arm64 and macOS are unverified.

The GC heap walk additionally needs the process's memory map, to find the GC descriptor and to
avoid reading a page that has been decommitted. That is implemented for Windows and Linux;
elsewhere it fails loudly rather than guessing. Type decoding and detouring are unaffected.

The contract descriptor is a diagnostics contract — deliberately versioned and far more stable than
raw offsets, but not a public API surface, and intended for *out-of-process* use. Reading it
in-process works because the target is the current process. Treat a version bump as a signal to
re-verify, which is what the fail-loud checks exist for.

.NET 11 moved the goalposts in ways worth recording, because each is a silent trap:

- **Contract versions became strings.** `"ExecutionManager": 2` is now `"ExecutionManager": "c2"`,
  and every contract in the 11.0 descriptor uses that form. Code that called `GetInt32()` on it
  threw. Both encodings are accepted now.
- **`MethodDescSizeTable` was removed.** Stepping through a `MethodDescChunk` needs a MethodDesc's
  size, and the table only precomputed it; the descriptor still publishes the size of every type it
  was made of, so `MethodDescSizes` reconstructs it. The chunk walk's `ChunkIndex` cross-check is
  what proved the reconstruction right.
- **MethodDesc gained a fourth optional slot.** `HasAsyncMethodData` (`Flags & 0x0040`) appends a
  24-byte `AsyncMethodData`, set on 1,379 of the 43,342 MethodDescs walked across CoreLib. Leaving
  it out desynchronises the rest of the chunk.
- **Async methods became runtime async.** With `runtime-async=on` there is no state machine to
  step: suspension is a heap `Continuation`, and the compiler emits no `<M>d__n` type at all.
- **A local variable's name is still nowhere in the runtime.** It never was, and .NET 11 did not
  change that — which is why the PDB reader exists.
- Also gone in 11.0: the `ObjectHeaderSize` global, and the `ArrayClass` and `GCHandle` types.

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
  ClrObject.cs                      entry point: ClrObject.From<T>()
  ClrEEClass.cs                     the cold half of a type
  ClrHeapObject.cs                  one object instance: its type and its size
  MemoryReader.cs                   offset-addressed reads
  Methods/
    ClrMethodTable.cs               the hot half, plus the MethodDescChunk walk
    ClrMethodDescription.cs         one method, token reconstruction, attributes
    ClrFieldDescription.cs          one field: offset, element type, storage
    ClrCodeVersion.cs               tiering: what the runtime has compiled
    MethodDescSizes.cs              MethodDesc sizes, rebuilt from the descriptor
  Metadata/
    ClrModuleMetadata.cs            the module's tables: names, tokens, attributes
    MetadataImage.cs                the table and heap layer, read in place
    MetadataSchema.cs               every table's columns, and the coded indexes
    ClrMethodSignature.cs           a signature, and its parameters
    ClrSignatureType.cs             a type as a signature spells it
    ClrCustomAttribute.cs           a row decoded against its constructor
    ClrModuleSymbols.cs             the portable PDB, and local names
    ClrInterfaceImplementation.cs   a declared interface, resolved to a MethodTable
  Il/
    ClrMethodIl.cs                  decode a body into instructions
    ClrMethodBodyImage.cs           the body in memory: header, code, EH sections
    ClrIlExceptionRegion.cs         a try/handler pair, from either source
    ClrIlLocal.cs                   one local slot: type, name, pinning
    ClrMethodCSharp.cs              the projection's public surface
    CSharpProjector.cs              the stack machine, undone
    CSharpStructurer.cs             the passes that undo the compiler's scaffolding
    MethodIlEmitter.cs              instructions back into a real method
    IlDumpStyle.cs                  the palette, and when colour is wanted
  Code/ClrCodeMap.cs                an address back to its method
  Detours/
    MethodDetour.cs                 redirect a method, restore on dispose
    MethodPrecode.cs                a method's precode and its dispatch slot
    MethodVtable.cs                 locate a virtual method's vtable slot
    MethodPairing.cs                whether a replacement can stand in at all
    DetourThunkSupport.cs           the generated adapter, emitted as IL
    PrecodeMachineInfo.cs           the runtime's own precode constants
    CodeProtection.cs               mprotect / VirtualProtect
  Gc/
    ClrGcHeap.cs                    entry point, and the object walk
    ClrGeneration.cs                the generation table, both GC flavours
    ClrHeapSegment.cs               one segment or region, and its bounds
    AllocationHoles.cs              the per-thread buffers a walk must step over
    GcWalkScope.cs                  hold off collection, and report if it happened
  Async/ClrContinuation.cs          a suspended await chain on the heap
  Threads/ClrThreadStore.cs         the runtime's managed thread list
  Exceptions/ClrExceptionTrace.cs   an exception's captured frames
  Loader/                           modules, assemblies, loader heaps

src/ClrSpectorConsole/              the tour: one section per capability
src/ClrSpectorTests/                TUnit tests, including the cross-checks
```

---

## Licence

MIT. See [LICENSE](LICENSE).
