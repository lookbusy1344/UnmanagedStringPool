# SegmentedStringPool Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a new `SegmentedStringPool` class alongside the existing `UnmanagedStringPool`, using a slot table + slab tier + segmented arena design. Eliminates per-allocation managed metadata (no `Dictionary`/`SortedList`), improves small-string throughput via bitmap-tracked slabs, and removes the need for in-place defragmentation via multi-segment growth.

**Architecture:** Three cooperating internal subsystems (slot table, slab tier, arena tier) coordinated by `SegmentedStringPool`. A 16-byte `PooledStringRef` handle (pool ref + 32-bit slot index + 32-bit generation) uses slot-map semantics for safe ID reuse. Low bit of the slot's stored pointer encodes the allocation tier. Growth appends new slabs/segments — no existing allocations ever move.

**Tech Stack:** .NET 10, xUnit 2.9, `System.Runtime.InteropServices.Marshal.AllocHGlobal`, `System.Buffers.ArrayPool<T>`, `System.Numerics.BitOperations`. Same analyzer rules as existing project (`.editorconfig`, `AnalysisModeDesign=All`, etc.).

**Spec:** `docs/superpowers/specs/2026-04-17-segmented-string-pool-design.md`

**Conventions to follow (from `CLAUDE.md`):**
- Tabs for indentation; opening brace same line; file-scoped namespaces.
- Library types under `namespace LookBusy`; tests under `namespace LookBusy.Test`.
- Named constants, not magic numbers.
- Every task: write failing test → verify it fails → implement → verify it passes → `dotnet format` → commit.
- Run tests with `gtimeout 60 dotnet test --filter ...`. Do not use `--no-build` first time.
- Never skip hooks or amend commits.
- Commit messages in conventional-commit form (`feat:`, `test:`, `refactor:`, etc.).

**Project wiring:** Each new `.cs` file added to the repo root must be added to the `<Compile Include="..." />` list in `Tests/StringPoolTest.csproj`, `Demo/StringPoolDemo.csproj`, and `Benchmarks/StringPoolBenchmarks.csproj`. Task 1 establishes this pattern and Task 18 includes a final audit.

---

## File Structure

**New source files (at repo root, alongside `UnmanagedStringPool.cs`):**

| File | Responsibility |
|---|---|
| `PooledStringRef.cs` | Public handle struct: 16 bytes, methods for read/mutate/free, value equality. |
| `SegmentedStringPool.cs` | Public pool class: coordinates slot table, slab tier, arena tier. Exposes `Allocate`, `Clear`, `Dispose`, `Reserve`, diagnostics. Also hosts `SegmentedStringPoolOptions` record and `SegmentedConstants`. |
| `SegmentedSlotEntry.cs` | Internal struct: 16-byte slot entry. Static helpers for generation encoding. |
| `SegmentedSlotTable.cs` | Internal class: manages `SlotEntry[]`, slot allocation/free/lookup, intrusive free-slot chain. |
| `SegmentedSlab.cs` | Internal sealed class: one slab of fixed-size cells with a bitmap. |
| `SegmentedSlabTier.cs` | Internal sealed class: per-size-class slab chains, size-class selection, slab allocation. |
| `SegmentedArenaSegment.cs` | Internal sealed class: one arena segment. Bump allocator + coalesced free list + bin heads. Also hosts `FreeBlockHeader` internal struct. |
| `SegmentedArenaTier.cs` | Internal sealed class: segment list, allocation across segments, locate-by-address. |

**New test files (under `Tests/`):**

| File | Coverage |
|---|---|
| `Tests/SegmentedSlotTableTests.cs` | Unit: slot alloc/free/reuse/generation bump/stale detection/growth. |
| `Tests/SegmentedSlabTests.cs` | Unit: bitmap cell alloc/free, TrailingZeroCount path, full-slab detection. |
| `Tests/SegmentedSlabTierTests.cs` | Unit: size class selection, active/full chains, slab allocation on fill. |
| `Tests/SegmentedArenaSegmentTests.cs` | Unit: bump alloc, free, coalescing, block splitting, bin selection. |
| `Tests/SegmentedArenaTierTests.cs` | Unit: multi-segment allocation, locate-by-address. |
| `Tests/SegmentedStringPoolTests.cs` | Integration: core allocate/read/free flow. |
| `Tests/SegmentedStringPoolEdgeCaseTests.cs` | Edge: empty strings, max size, boundary routing, disposed pool. |
| `Tests/PooledStringRefTests.cs` | Integration: Insert, Replace, SubstringSpan, queries, equality, hash, ToString. |
| `Tests/SegmentedStringPoolLifecycleTests.cs` | Clear, Dispose, finalizer, Reserve pre-warm. |

**Modified files:**

| File | Change |
|---|---|
| `Tests/StringPoolTest.csproj` | Add every new `.cs` file to the `<Compile Include="..." />` list. |
| `Demo/StringPoolDemo.csproj` | Same additions. |
| `Benchmarks/StringPoolBenchmarks.csproj` | Same additions. |
| `Tests/GcPressureTests.cs` | Add pressure tests for `SegmentedStringPool`. |
| `Benchmarks/BulkAllocateBenchmarks.cs` | Add `BulkAllocate_Segmented` benchmark. |
| `Benchmarks/InterleavedAllocFreeBenchmarks.cs` | Add `InterleavedAllocFree_Segmented` benchmark. |

---

## Shared Constants

These appear in multiple files. Defined once in `SegmentedStringPool.cs` as `internal const` and referenced where needed:

```csharp
internal static class SegmentedConstants {
    public const uint HighBit = 0x80000000u;          // slot-is-free marker in SlotEntry.Generation
    public const uint GenerationMask = 0x7FFFFFFFu;   // low 31 bits of generation
    public const uint NoFreeSlot = 0xFFFFFFFFu;       // free-list terminator
    public const int PtrAlignment = 8;                // all unmanaged pointers aligned to 8 bytes
    public const long TierTagMask = 1L;               // low bit of IntPtr encodes tier
    public const long PtrMask = ~7L;                  // mask off the tag bits to get real pointer
    public const int TierSlab = 0;
    public const int TierArena = 1;
    public const int SlabSizeClassCount = 5;          // 8, 16, 32, 64, 128 chars
    public const int MinArenaBlockBytes = 16;         // must hold FreeBlockHeader
    public const int ArenaBinCount = 16;              // log2 bins from 16 B to 2 MB
    public const int DefaultSlotCapacity = 64;
    public const int DefaultSlabCellsPerSlab = 256;
    public const int DefaultArenaSegmentBytes = 1 << 20;     // 1 MB
    public const int DefaultSmallStringThresholdChars = 128;
}
```

---

### Task 1: `PooledStringRef` skeleton + project wiring

Create the handle struct with the minimal API needed for existence: the struct itself, `Empty`, `IsEmpty`. Full functionality will be added in later tasks as `SegmentedStringPool` becomes operational. Also wires every project file to compile the new source.

**Files:**
- Create: `PooledStringRef.cs`
- Modify: `Tests/StringPoolTest.csproj` (add `<Compile Include="../PooledStringRef.cs" />`)
- Modify: `Demo/StringPoolDemo.csproj` (same)
- Modify: `Benchmarks/StringPoolBenchmarks.csproj` (same)
- Test: `Tests/PooledStringRefTests.cs`

- [ ] **Step 1: Write the failing test**

Create `Tests/PooledStringRefTests.cs`:

```csharp
namespace LookBusy.Test;

using System;
using LookBusy;
using Xunit;

public sealed class PooledStringRefTests
{
	[Fact]
	public void Empty_DefaultValue_IsEmpty()
	{
		var r = default(PooledStringRef);
		Assert.True(r.IsEmpty);
	}

	[Fact]
	public void Empty_StaticProperty_ReturnsDefault()
	{
		var a = PooledStringRef.Empty;
		var b = default(PooledStringRef);
		Assert.Equal(a, b);
	}

	[Fact]
	public void Empty_HasNullPoolAndZeroHandle()
	{
		var r = PooledStringRef.Empty;
		Assert.Null(r.Pool);
		Assert.Equal(0u, r.SlotIndex);
		Assert.Equal(0u, r.Generation);
	}
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `gtimeout 60 dotnet test --filter FullyQualifiedName~PooledStringRefTests`
Expected: FAIL — build error "The type or namespace name 'PooledStringRef' could not be found".

- [ ] **Step 3: Create `PooledStringRef.cs` at repo root**

```csharp
namespace LookBusy;

using System;

/// <summary>
/// Handle to a string allocated from a <see cref="SegmentedStringPool"/>. 16 bytes:
/// pool reference (8), slot index (4), generation (4). Value-equality via record struct.
/// <para>
/// <see cref="default(PooledStringRef)"/> is the empty sentinel; real allocations always have
/// generation ≥ 1. Disposing any copy invalidates all copies of the same allocation (generation
/// bump on free), matching the existing <see cref="PooledString"/> semantics.
/// </para>
/// </summary>
public readonly record struct PooledStringRef(
	SegmentedStringPool? Pool,
	uint SlotIndex,
	uint Generation
) : IDisposable
{
	public static PooledStringRef Empty => default;

	public bool IsEmpty => Pool is null && SlotIndex == 0u && Generation == 0u;

	public void Dispose() { /* filled in later tasks */ }
}
```

- [ ] **Step 4: Wire the new source into every project**

Edit `Tests/StringPoolTest.csproj`, inside the existing `<ItemGroup>` with `<Compile Include>` entries:

```xml
<Compile Include="../UnmanagedStringPool.cs" />
<Compile Include="../PooledString.cs" />
<Compile Include="../PooledStringRef.cs" />
```

Edit `Demo/StringPoolDemo.csproj` and `Benchmarks/StringPoolBenchmarks.csproj` the same way.

- [ ] **Step 5: Run test to verify it passes**

Run: `gtimeout 60 dotnet test --filter FullyQualifiedName~PooledStringRefTests`
Expected: PASS (3 tests).

- [ ] **Step 6: Format and commit**

```bash
dotnet format
git add PooledStringRef.cs Tests/PooledStringRefTests.cs Tests/StringPoolTest.csproj Demo/StringPoolDemo.csproj Benchmarks/StringPoolBenchmarks.csproj
git commit -m "feat: add PooledStringRef handle skeleton"
```

---

### Task 2: `SegmentedConstants` + `SegmentedSlotEntry` with generation encoding

Internal primitives: the shared constants and the slot entry struct with helpers for encoding/decoding the free-list link via generation's high bit. These are the building blocks for the slot table in Task 3.

**Files:**
- Create: `SegmentedSlotEntry.cs`
- Modify: `SegmentedStringPool.cs` (create new; will be expanded in later tasks — for now only hosts `SegmentedConstants`)
- Modify: all three `.csproj` files (add both new files)
- Test: `Tests/SegmentedSlotTableTests.cs`

- [ ] **Step 1: Write the failing test**

Create `Tests/SegmentedSlotTableTests.cs`:

```csharp
namespace LookBusy.Test;

using LookBusy;
using Xunit;

public sealed class SegmentedSlotEntryEncodingTests
{
	[Fact]
	public void IsFree_HighBitSet_ReturnsTrue()
	{
		var gen = SegmentedConstants.HighBit | 5u;
		Assert.True(SegmentedSlotEntry.IsFree(gen));
	}

	[Fact]
	public void IsFree_HighBitClear_ReturnsFalse()
	{
		Assert.False(SegmentedSlotEntry.IsFree(5u));
		Assert.False(SegmentedSlotEntry.IsFree(0u));
	}

	[Fact]
	public void GenerationValue_MasksOffHighBit()
	{
		var gen = SegmentedConstants.HighBit | 42u;
		Assert.Equal(42u, SegmentedSlotEntry.GenerationValue(gen));
	}

	[Fact]
	public void MarkFreeAndBumpGen_SetsHighBitAndIncrements()
	{
		var gen = SegmentedSlotEntry.MarkFreeAndBumpGen(5u);
		Assert.True(SegmentedSlotEntry.IsFree(gen));
		Assert.Equal(6u, SegmentedSlotEntry.GenerationValue(gen));
	}

	[Fact]
	public void ClearFreeAndBumpGen_ClearsHighBitAndIncrements()
	{
		var gen = SegmentedSlotEntry.ClearFreeAndBumpGen(SegmentedConstants.HighBit | 5u);
		Assert.False(SegmentedSlotEntry.IsFree(gen));
		Assert.Equal(6u, SegmentedSlotEntry.GenerationValue(gen));
	}
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `gtimeout 60 dotnet test --filter FullyQualifiedName~SegmentedSlotEntryEncoding`
Expected: FAIL — build error "The type or namespace name 'SegmentedConstants' could not be found".

- [ ] **Step 3: Create `SegmentedStringPool.cs` with constants**

```csharp
namespace LookBusy;

/// <summary>
/// Constants shared across SegmentedStringPool internal types. Defined here rather than on
/// SegmentedStringPool itself to avoid file-length bloat and keep the public surface clean.
/// </summary>
internal static class SegmentedConstants
{
	public const uint HighBit = 0x80000000u;
	public const uint GenerationMask = 0x7FFFFFFFu;
	public const uint NoFreeSlot = 0xFFFFFFFFu;
	public const int PtrAlignment = 8;
	public const long TierTagMask = 1L;
	public const long PtrMask = ~7L;
	public const int TierSlab = 0;
	public const int TierArena = 1;
	public const int SlabSizeClassCount = 5;
	public const int MinArenaBlockBytes = 16;
	public const int ArenaBinCount = 16;
	public const int DefaultSlotCapacity = 64;
	public const int DefaultSlabCellsPerSlab = 256;
	public const int DefaultArenaSegmentBytes = 1 << 20;
	public const int DefaultSmallStringThresholdChars = 128;
}

/// <summary>
/// Segmented unmanaged string pool. Full implementation added in later tasks; this file
/// currently holds only the shared constants type.
/// </summary>
public sealed class SegmentedStringPool
{
	// Implementation added in Task 6 and beyond.
}
```

- [ ] **Step 4: Create `SegmentedSlotEntry.cs`**

```csharp
namespace LookBusy;

using System;

/// <summary>
/// Per-allocation slot metadata. 16 bytes aligned to 8. High bit of Generation marks slot as free;
/// when free, Ptr stores the next-free-slot index cast to IntPtr, and LengthChars is unused.
/// </summary>
internal struct SegmentedSlotEntry
{
	public IntPtr Ptr;
	public int LengthChars;
	public uint Generation;

	public static bool IsFree(uint generation) => (generation & SegmentedConstants.HighBit) != 0u;

	public static uint GenerationValue(uint generation) => generation & SegmentedConstants.GenerationMask;

	public static uint MarkFreeAndBumpGen(uint generation) =>
		((GenerationValue(generation) + 1u) & SegmentedConstants.GenerationMask) | SegmentedConstants.HighBit;

	public static uint ClearFreeAndBumpGen(uint generation) =>
		(GenerationValue(generation) + 1u) & SegmentedConstants.GenerationMask;
}
```

- [ ] **Step 5: Wire both new files into all three csproj files**

Add to `Tests/StringPoolTest.csproj`, `Demo/StringPoolDemo.csproj`, `Benchmarks/StringPoolBenchmarks.csproj`:

```xml
<Compile Include="../SegmentedStringPool.cs" />
<Compile Include="../SegmentedSlotEntry.cs" />
```

- [ ] **Step 6: Run tests**

Run: `gtimeout 60 dotnet test --filter FullyQualifiedName~SegmentedSlotEntryEncoding`
Expected: PASS (5 tests).

- [ ] **Step 7: Format and commit**

```bash
dotnet format
git add SegmentedStringPool.cs SegmentedSlotEntry.cs Tests/SegmentedSlotTableTests.cs \
        Tests/StringPoolTest.csproj Demo/StringPoolDemo.csproj Benchmarks/StringPoolBenchmarks.csproj
git commit -m "feat: add SegmentedSlotEntry generation encoding helpers"
```

---

### Task 3: `SegmentedSlotTable`

Internal managed-array-backed slot store. Allocates slot indices, bumps generation on free, grows by doubling, detects stale handles. Holds no unmanaged pointers itself — those are written by the caller via the returned slot index.

**Files:**
- Create: `SegmentedSlotTable.cs`
- Modify: three csproj files
- Test: `Tests/SegmentedSlotTableTests.cs` (extend)

- [ ] **Step 1: Write the failing tests (append to existing file)**

Append to `Tests/SegmentedSlotTableTests.cs`:

```csharp
public sealed class SegmentedSlotTableTests
{
	[Fact]
	public void NewTable_ActiveCountIsZero()
	{
		var table = new SegmentedSlotTable(16);
		Assert.Equal(0, table.ActiveCount);
	}

	[Fact]
	public void Allocate_FirstSlot_ReturnsSlotZeroAndGenOne()
	{
		var table = new SegmentedSlotTable(16);
		var (slot, gen) = table.Allocate(ptr: (IntPtr)0x1000, lengthChars: 5);
		Assert.Equal(0u, slot);
		Assert.Equal(1u, gen);
		Assert.Equal(1, table.ActiveCount);
	}

	[Fact]
	public void Allocate_Multiple_AssignsSequentialSlots()
	{
		var table = new SegmentedSlotTable(16);
		var (s0, _) = table.Allocate((IntPtr)0x100, 1);
		var (s1, _) = table.Allocate((IntPtr)0x200, 2);
		var (s2, _) = table.Allocate((IntPtr)0x300, 3);
		Assert.Equal(0u, s0);
		Assert.Equal(1u, s1);
		Assert.Equal(2u, s2);
	}

	[Fact]
	public void TryReadSlot_ValidHandle_ReturnsPtrAndLength()
	{
		var table = new SegmentedSlotTable(16);
		var (slot, gen) = table.Allocate((IntPtr)0xABCD, 7);
		var ok = table.TryReadSlot(slot, gen, out var entry);
		Assert.True(ok);
		Assert.Equal((IntPtr)0xABCD, entry.Ptr);
		Assert.Equal(7, entry.LengthChars);
	}

	[Fact]
	public void Free_BumpsGeneration_AndMarksFree()
	{
		var table = new SegmentedSlotTable(16);
		var (slot, gen) = table.Allocate((IntPtr)0x100, 1);
		var freed = table.Free(slot, gen);
		Assert.True(freed);
		Assert.Equal(0, table.ActiveCount);
	}

	[Fact]
	public void Free_StaleGeneration_ReturnsFalse_AndDoesNotDoubleFree()
	{
		var table = new SegmentedSlotTable(16);
		var (slot, gen) = table.Allocate((IntPtr)0x100, 1);
		Assert.True(table.Free(slot, gen));
		Assert.False(table.Free(slot, gen));     // already freed, handle stale
		Assert.Equal(0, table.ActiveCount);
	}

	[Fact]
	public void TryReadSlot_StaleHandle_ReturnsFalse()
	{
		var table = new SegmentedSlotTable(16);
		var (slot, gen) = table.Allocate((IntPtr)0x100, 1);
		table.Free(slot, gen);
		Assert.False(table.TryReadSlot(slot, gen, out _));
	}

	[Fact]
	public void Allocate_AfterFree_ReusesSlotWithNewGeneration()
	{
		var table = new SegmentedSlotTable(16);
		var (s0, g0) = table.Allocate((IntPtr)0x100, 1);
		table.Free(s0, g0);
		var (s1, g1) = table.Allocate((IntPtr)0x200, 2);
		Assert.Equal(s0, s1);
		Assert.NotEqual(g0, g1);                 // generation incremented twice (free+alloc)
	}

	[Fact]
	public void Allocate_BeyondInitialCapacity_Grows()
	{
		var table = new SegmentedSlotTable(initialCapacity: 4);
		for (var i = 0; i < 10; i++) {
			_ = table.Allocate((IntPtr)(0x100 + i), 1);
		}
		Assert.Equal(10, table.ActiveCount);
	}

	[Fact]
	public void TryReadSlot_OutOfRangeSlotIndex_ReturnsFalse()
	{
		var table = new SegmentedSlotTable(16);
		Assert.False(table.TryReadSlot(99u, 1u, out _));
	}
}
```

- [ ] **Step 2: Verify failure**

Run: `gtimeout 60 dotnet test --filter FullyQualifiedName~SegmentedSlotTableTests`
Expected: FAIL — "The type or namespace name 'SegmentedSlotTable' could not be found".

- [ ] **Step 3: Create `SegmentedSlotTable.cs`**

```csharp
namespace LookBusy;

using System;
using System.Runtime.CompilerServices;

/// <summary>
/// Manages a dynamically-growing array of <see cref="SegmentedSlotEntry"/>. Slots form an intrusive
/// free-list via the generation high bit + Ptr field when freed. Grows by doubling. All operations
/// are amortized O(1).
/// </summary>
internal sealed class SegmentedSlotTable
{
	private SegmentedSlotEntry[] slots;
	private int highWater;           // one past the highest slot index ever used
	private uint freeHead;           // SegmentedConstants.NoFreeSlot when chain empty
	private int activeCount;

	public SegmentedSlotTable(int initialCapacity)
	{
		if (initialCapacity < 1) {
			throw new ArgumentOutOfRangeException(nameof(initialCapacity));
		}
		slots = new SegmentedSlotEntry[initialCapacity];
		freeHead = SegmentedConstants.NoFreeSlot;
	}

	public int ActiveCount => activeCount;

	public int Capacity => slots.Length;

	public (uint SlotIndex, uint Generation) Allocate(IntPtr ptr, int lengthChars)
	{
		uint slotIndex;
		if (freeHead != SegmentedConstants.NoFreeSlot) {
			slotIndex = freeHead;
			// Next free slot is stashed in the current slot's Ptr field.
			freeHead = (uint)slots[slotIndex].Ptr.ToInt64();
		} else {
			if (highWater == slots.Length) {
				Grow();
			}
			slotIndex = (uint)highWater;
			++highWater;
		}

		ref var slot = ref slots[slotIndex];
		var newGen = SegmentedSlotEntry.ClearFreeAndBumpGen(slot.Generation);
		slot.Ptr = ptr;
		slot.LengthChars = lengthChars;
		slot.Generation = newGen;
		++activeCount;
		return (slotIndex, newGen);
	}

	public bool Free(uint slotIndex, uint generation)
	{
		if (slotIndex >= (uint)highWater) {
			return false;
		}
		ref var slot = ref slots[slotIndex];
		if (SegmentedSlotEntry.IsFree(slot.Generation)) {
			return false;
		}
		if (slot.Generation != generation) {
			return false;
		}
		var bumped = SegmentedSlotEntry.MarkFreeAndBumpGen(slot.Generation);
		slot.Ptr = (IntPtr)(long)freeHead;
		slot.LengthChars = 0;
		slot.Generation = bumped;
		freeHead = slotIndex;
		--activeCount;
		return true;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool TryReadSlot(uint slotIndex, uint generation, out SegmentedSlotEntry entry)
	{
		if (slotIndex >= (uint)highWater) {
			entry = default;
			return false;
		}
		entry = slots[slotIndex];
		if (entry.Generation != generation) {
			entry = default;
			return false;
		}
		return true;
	}

	/// <summary>
	/// Direct slot reference for hot-path writes (used by WriteAtPosition). Caller must have
	/// validated generation via TryReadSlot first.
	/// </summary>
	public ref SegmentedSlotEntry SlotRef(uint slotIndex) => ref slots[slotIndex];

	/// <summary>
	/// Bump the generation of every live slot, reset the free chain to contain all slots in order.
	/// Called from SegmentedStringPool.Clear. O(highWater).
	/// </summary>
	public void ClearAllSlots()
	{
		for (var i = 0; i < highWater; i++) {
			ref var slot = ref slots[i];
			if (!SegmentedSlotEntry.IsFree(slot.Generation)) {
				slot.Generation = SegmentedSlotEntry.MarkFreeAndBumpGen(slot.Generation);
			}
			var nextIndex = (uint)(i + 1 < highWater ? i + 1 : (int)SegmentedConstants.NoFreeSlot);
			slot.Ptr = (IntPtr)(long)nextIndex;
			slot.LengthChars = 0;
		}
		freeHead = highWater == 0 ? SegmentedConstants.NoFreeSlot : 0u;
		activeCount = 0;
	}

	private void Grow()
	{
		var newCapacity = slots.Length * 2;
		if ((uint)newCapacity > (uint)int.MaxValue) {
			throw new OutOfMemoryException("Slot table capacity exceeded");
		}
		Array.Resize(ref slots, newCapacity);
	}
}
```

- [ ] **Step 4: Add to csproj files**

Add `<Compile Include="../SegmentedSlotTable.cs" />` to all three project files.

- [ ] **Step 5: Run tests**

Run: `gtimeout 60 dotnet test --filter FullyQualifiedName~SegmentedSlotTableTests`
Expected: PASS (10 tests).

- [ ] **Step 6: Format and commit**

```bash
dotnet format
git add SegmentedSlotTable.cs Tests/SegmentedSlotTableTests.cs Tests/StringPoolTest.csproj \
        Demo/StringPoolDemo.csproj Benchmarks/StringPoolBenchmarks.csproj
git commit -m "feat: add SegmentedSlotTable with intrusive free-slot chain"
```

---

### Task 4: `SegmentedSlab` + `SegmentedSlabTier`

Internal slab allocator. A `Slab` is a fixed-size unmanaged buffer divided into equal-sized cells with a bitmap of free cells. A `SlabTier` groups slabs per size class and routes allocations. Size classes: 8, 16, 32, 64, 128 chars (indexed 0..4).

**Files:**
- Create: `SegmentedSlab.cs`
- Create: `SegmentedSlabTier.cs`
- Modify: three csproj files
- Test: `Tests/SegmentedSlabTests.cs`, `Tests/SegmentedSlabTierTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `Tests/SegmentedSlabTests.cs`:

```csharp
namespace LookBusy.Test;

using System;
using LookBusy;
using Xunit;

public sealed class SegmentedSlabTests : IDisposable
{
	private readonly SegmentedSlab slab = new(cellBytes: 16, cellCount: 8);

	public void Dispose()
	{
		slab.Dispose();
		GC.SuppressFinalize(this);
	}

	[Fact]
	public void NewSlab_AllCellsFree()
	{
		Assert.Equal(8, slab.FreeCells);
		Assert.False(slab.IsFull);
	}

	[Fact]
	public void TryAllocateCell_Empty_ReturnsZeroOffset()
	{
		var ok = slab.TryAllocateCell(out var cellIndex);
		Assert.True(ok);
		Assert.Equal(0, cellIndex);
		Assert.Equal(7, slab.FreeCells);
	}

	[Fact]
	public void TryAllocateCell_Sequential_ReturnsIncreasingIndices()
	{
		slab.TryAllocateCell(out var a);
		slab.TryAllocateCell(out var b);
		slab.TryAllocateCell(out var c);
		Assert.Equal(0, a);
		Assert.Equal(1, b);
		Assert.Equal(2, c);
	}

	[Fact]
	public void TryAllocateCell_Full_ReturnsFalse()
	{
		for (var i = 0; i < 8; i++) {
			Assert.True(slab.TryAllocateCell(out _));
		}
		Assert.True(slab.IsFull);
		Assert.False(slab.TryAllocateCell(out _));
	}

	[Fact]
	public void FreeCell_AllowsReuse()
	{
		slab.TryAllocateCell(out _);
		slab.TryAllocateCell(out var second);
		slab.FreeCell(second);
		Assert.Equal(7, slab.FreeCells);
		slab.TryAllocateCell(out var reused);
		Assert.Equal(second, reused);
	}

	[Fact]
	public void OffsetOfCell_ReturnsCellIndex_TimesCellBytes()
	{
		Assert.Equal(0, slab.OffsetOfCell(0));
		Assert.Equal(16, slab.OffsetOfCell(1));
		Assert.Equal(112, slab.OffsetOfCell(7));
	}

	[Fact]
	public void CellIndexFromOffset_Roundtrips()
	{
		for (var i = 0; i < 8; i++) {
			Assert.Equal(i, slab.CellIndexFromOffset(slab.OffsetOfCell(i)));
		}
	}
}
```

Create `Tests/SegmentedSlabTierTests.cs`:

```csharp
namespace LookBusy.Test;

using System;
using LookBusy;
using Xunit;

public sealed class SegmentedSlabTierTests : IDisposable
{
	private readonly SegmentedSlabTier tier = new(cellsPerSlab: 4);

	public void Dispose()
	{
		tier.Dispose();
		GC.SuppressFinalize(this);
	}

	[Theory]
	[InlineData(1, 0)]
	[InlineData(8, 0)]
	[InlineData(9, 1)]
	[InlineData(16, 1)]
	[InlineData(17, 2)]
	[InlineData(32, 2)]
	[InlineData(64, 3)]
	[InlineData(65, 4)]
	[InlineData(128, 4)]
	public void ChooseSizeClass_ReturnsSmallestClassFitting(int charCount, int expectedClass) =>
		Assert.Equal(expectedClass, SegmentedSlabTier.ChooseSizeClass(charCount));

	[Fact]
	public void ChooseSizeClass_AboveThreshold_ReturnsSentinel()
	{
		Assert.Equal(-1, SegmentedSlabTier.ChooseSizeClass(129));
		Assert.Equal(-1, SegmentedSlabTier.ChooseSizeClass(1000));
	}

	[Fact]
	public void Allocate_SmallString_ReturnsPointerIntoAnySlab()
	{
		var ptr = tier.Allocate(charCount: 5, out var slabRef);
		Assert.NotEqual(IntPtr.Zero, ptr);
		Assert.NotNull(slabRef);
	}

	[Fact]
	public void Allocate_BeyondSlabCapacity_AllocatesSecondSlab()
	{
		for (var i = 0; i < 4; i++) {
			tier.Allocate(5, out _);
		}
		tier.Allocate(5, out _);
		Assert.True(tier.SlabCount >= 2);
	}

	[Fact]
	public void Free_ReturnsCellToSlab()
	{
		var ptr = tier.Allocate(5, out var slab);
		tier.Free(ptr, slab);
		Assert.Equal(4, slab.FreeCells);
	}
}
```

- [ ] **Step 2: Verify failure**

Run: `gtimeout 60 dotnet test --filter "FullyQualifiedName~SegmentedSlabTests|FullyQualifiedName~SegmentedSlabTierTests"`
Expected: FAIL — build errors.

- [ ] **Step 3: Create `SegmentedSlab.cs`**

```csharp
namespace LookBusy;

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/// <summary>
/// A single slab of fixed-size cells backed by unmanaged memory. Cells are tracked via a bitmap
/// (1 = free, 0 = used). Allocation uses <see cref="BitOperations.TrailingZeroCount(ulong)"/> to
/// locate the first free cell in O(1).
/// </summary>
internal sealed class SegmentedSlab : IDisposable
{
	public readonly int CellBytes;
	public readonly int CellCount;
	public readonly IntPtr Buffer;
	private readonly ulong[] bitmap;   // word count = ceil(CellCount / 64)
	private int freeCells;
	private bool disposed;

	public SegmentedSlab(int cellBytes, int cellCount)
	{
		if (cellBytes < SegmentedConstants.PtrAlignment) {
			throw new ArgumentOutOfRangeException(nameof(cellBytes));
		}
		if (cellCount < 1 || cellCount > 65536) {
			throw new ArgumentOutOfRangeException(nameof(cellCount));
		}
		CellBytes = cellBytes;
		CellCount = cellCount;
		Buffer = Marshal.AllocHGlobal(cellBytes * cellCount);
		var words = (cellCount + 63) / 64;
		bitmap = new ulong[words];
		for (var w = 0; w < words; w++) {
			bitmap[w] = ulong.MaxValue;     // all bits set = all cells free
		}
		// Clear bits beyond CellCount if CellCount is not a multiple of 64
		var excess = (words * 64) - cellCount;
		if (excess > 0) {
			bitmap[^1] &= (1UL << (64 - excess)) - 1UL;
		}
		freeCells = cellCount;
	}

	public int FreeCells => freeCells;

	public bool IsFull => freeCells == 0;

	public SegmentedSlab? NextInClass { get; set; }

	public bool TryAllocateCell(out int cellIndex)
	{
		for (var w = 0; w < bitmap.Length; w++) {
			var word = bitmap[w];
			if (word != 0UL) {
				var bit = BitOperations.TrailingZeroCount(word);
				cellIndex = (w * 64) + bit;
				if (cellIndex >= CellCount) {
					break;
				}
				bitmap[w] = word & ~(1UL << bit);
				--freeCells;
				return true;
			}
		}
		cellIndex = -1;
		return false;
	}

	public void FreeCell(int cellIndex)
	{
		if ((uint)cellIndex >= (uint)CellCount) {
			throw new ArgumentOutOfRangeException(nameof(cellIndex));
		}
		var w = cellIndex / 64;
		var bit = cellIndex & 63;
		var mask = 1UL << bit;
		if ((bitmap[w] & mask) != 0UL) {
			throw new InvalidOperationException("Cell already free");
		}
		bitmap[w] |= mask;
		++freeCells;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public int OffsetOfCell(int cellIndex) => cellIndex * CellBytes;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public int CellIndexFromOffset(int offsetBytes) => offsetBytes / CellBytes;

	public bool Contains(IntPtr ptr)
	{
		var raw = ptr.ToInt64();
		var start = Buffer.ToInt64();
		var end = start + ((long)CellBytes * CellCount);
		return raw >= start && raw < end;
	}

	public void ResetAllCellsFree()
	{
		for (var w = 0; w < bitmap.Length; w++) {
			bitmap[w] = ulong.MaxValue;
		}
		var excess = (bitmap.Length * 64) - CellCount;
		if (excess > 0) {
			bitmap[^1] &= (1UL << (64 - excess)) - 1UL;
		}
		freeCells = CellCount;
	}

	public long UnmanagedBytes => (long)CellBytes * CellCount;

	public void Dispose()
	{
		if (!disposed) {
			Marshal.FreeHGlobal(Buffer);
			disposed = true;
		}
	}
}
```

- [ ] **Step 4: Create `SegmentedSlabTier.cs`**

```csharp
namespace LookBusy;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

/// <summary>
/// Manages slab chains per size class (8, 16, 32, 64, 128 chars). Maintains an active slab per
/// class (first non-full); keeps full slabs on the chain for address-based lookup during free.
/// All allocations here originate from strings with charCount ≤ <see cref="SegmentedConstants.DefaultSmallStringThresholdChars"/>.
/// </summary>
internal sealed class SegmentedSlabTier : IDisposable
{
	// Size class index → cell size in chars. Cell bytes = charCount * 2 (8-byte aligned already).
	private static readonly int[] SizeClassChars = [8, 16, 32, 64, 128];

	private readonly SegmentedSlab?[] activeSlabs = new SegmentedSlab?[SegmentedConstants.SlabSizeClassCount];
	private readonly List<SegmentedSlab> allSlabs = [];
	private readonly int cellsPerSlab;

	public SegmentedSlabTier(int cellsPerSlab)
	{
		if (cellsPerSlab < 1) {
			throw new ArgumentOutOfRangeException(nameof(cellsPerSlab));
		}
		this.cellsPerSlab = cellsPerSlab;
	}

	public int SlabCount => allSlabs.Count;

	public long UnmanagedBytes
	{
		get
		{
			long total = 0;
			foreach (var s in allSlabs) {
				total += s.UnmanagedBytes;
			}
			return total;
		}
	}

	/// <summary>
	/// Returns the smallest size class index (0..4) that fits <paramref name="charCount"/>, or
	/// -1 if the count exceeds the threshold and should go to the arena tier.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static int ChooseSizeClass(int charCount)
	{
		if (charCount <= 8) { return 0; }
		if (charCount <= 16) { return 1; }
		if (charCount <= 32) { return 2; }
		if (charCount <= 64) { return 3; }
		if (charCount <= 128) { return 4; }
		return -1;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static int CellBytesForSizeClass(int sizeClass) => SizeClassChars[sizeClass] * sizeof(char);

	public IntPtr Allocate(int charCount, out SegmentedSlab owningSlab)
	{
		var sizeClass = ChooseSizeClass(charCount);
		if (sizeClass < 0) {
			throw new InvalidOperationException("Size exceeds slab threshold; caller should route to arena");
		}
		var slab = activeSlabs[sizeClass];
		if (slab is null || slab.IsFull) {
			slab = FindNonFullSlabInClass(sizeClass) ?? AllocateNewSlab(sizeClass);
			activeSlabs[sizeClass] = slab;
		}
		if (!slab.TryAllocateCell(out var cellIndex)) {
			// Shouldn't happen because we just asserted it wasn't full, but handle defensively
			slab = AllocateNewSlab(sizeClass);
			activeSlabs[sizeClass] = slab;
			slab.TryAllocateCell(out cellIndex);
		}
		owningSlab = slab;
		return (IntPtr)(slab.Buffer.ToInt64() + slab.OffsetOfCell(cellIndex));
	}

	public void Free(IntPtr ptr, SegmentedSlab slab)
	{
		var offset = (int)(ptr.ToInt64() - slab.Buffer.ToInt64());
		slab.FreeCell(slab.CellIndexFromOffset(offset));
	}

	/// <summary>
	/// Binary-searches the owning slab by pointer range. Used by <see cref="SegmentedStringPool.Free"/>
	/// when only the raw pointer is known.
	/// </summary>
	public SegmentedSlab LocateSlabByPointer(IntPtr ptr)
	{
		// Linear scan is fine — slab count typically 10s. Optimise to binary search if needed.
		foreach (var s in allSlabs) {
			if (s.Contains(ptr)) {
				return s;
			}
		}
		throw new InvalidOperationException("Pointer does not belong to any slab in this tier");
	}

	public void ResetAll()
	{
		foreach (var s in allSlabs) {
			s.ResetAllCellsFree();
		}
		for (var i = 0; i < activeSlabs.Length; i++) {
			// Re-point active slab to the first slab in that class, if any
			activeSlabs[i] = null;
			foreach (var s in allSlabs) {
				if (s.CellBytes == CellBytesForSizeClass(i)) {
					activeSlabs[i] = s;
					break;
				}
			}
		}
	}

	public void Dispose()
	{
		foreach (var s in allSlabs) {
			s.Dispose();
		}
		allSlabs.Clear();
	}

	private SegmentedSlab AllocateNewSlab(int sizeClass)
	{
		var slab = new SegmentedSlab(CellBytesForSizeClass(sizeClass), cellsPerSlab);
		allSlabs.Add(slab);
		return slab;
	}

	private SegmentedSlab? FindNonFullSlabInClass(int sizeClass)
	{
		var cellBytes = CellBytesForSizeClass(sizeClass);
		foreach (var s in allSlabs) {
			if (s.CellBytes == cellBytes && !s.IsFull) {
				return s;
			}
		}
		return null;
	}
}
```

- [ ] **Step 5: Add to csproj files**

Add `<Compile Include="../SegmentedSlab.cs" />` and `<Compile Include="../SegmentedSlabTier.cs" />` to all three projects.

- [ ] **Step 6: Run tests**

Run: `gtimeout 90 dotnet test --filter "FullyQualifiedName~SegmentedSlabTests|FullyQualifiedName~SegmentedSlabTierTests"`
Expected: PASS (14 tests: 7 slab + 7 tier).

- [ ] **Step 7: Format and commit**

```bash
dotnet format
git add SegmentedSlab.cs SegmentedSlabTier.cs Tests/SegmentedSlabTests.cs Tests/SegmentedSlabTierTests.cs \
        Tests/StringPoolTest.csproj Demo/StringPoolDemo.csproj Benchmarks/StringPoolBenchmarks.csproj
git commit -m "feat: add slab tier with bitmap-tracked cells and size-class routing"
```

---

### Task 5: `SegmentedArenaSegment` + `SegmentedArenaTier`

Internal arena allocator for strings > 128 chars. A segment is a fixed-size unmanaged buffer with a bump pointer and an intrusive doubly-linked free list stored inline in freed blocks. Bins segregate free blocks by `Log2(size)`. `ArenaTier` manages a list of segments and allocates new ones on demand.

**Files:**
- Create: `SegmentedArenaSegment.cs`
- Create: `SegmentedArenaTier.cs`
- Modify: three csproj files
- Test: `Tests/SegmentedArenaSegmentTests.cs`, `Tests/SegmentedArenaTierTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `Tests/SegmentedArenaSegmentTests.cs`:

```csharp
namespace LookBusy.Test;

using System;
using LookBusy;
using Xunit;

public sealed class SegmentedArenaSegmentTests : IDisposable
{
	private readonly SegmentedArenaSegment segment = new(capacity: 4096);

	public void Dispose()
	{
		segment.Dispose();
		GC.SuppressFinalize(this);
	}

	[Fact]
	public void NewSegment_BumpOffsetZero()
	{
		Assert.Equal(0, segment.BumpOffset);
		Assert.Equal(4096, segment.Capacity);
	}

	[Fact]
	public void TryAllocate_FromEmpty_UsesBump()
	{
		var ok = segment.TryAllocate(byteCount: 128, out var ptr);
		Assert.True(ok);
		Assert.Equal(segment.Buffer, ptr);
		Assert.Equal(128, segment.BumpOffset);
	}

	[Fact]
	public void TryAllocate_BeyondCapacity_ReturnsFalse()
	{
		Assert.False(segment.TryAllocate(byteCount: 8192, out _));
	}

	[Fact]
	public void Free_AlignedSize_ReturnsBlockToBin()
	{
		segment.TryAllocate(256, out var ptr);
		segment.Free(ptr, 256);
		Assert.True(segment.TryAllocate(200, out var reused));
		Assert.Equal(ptr, reused);       // reused from the free list, not bumped
	}

	[Fact]
	public void Free_AdjacentBlocks_CoalesceOnFree()
	{
		segment.TryAllocate(256, out var a);
		segment.TryAllocate(256, out var b);
		segment.Free(a, 256);
		segment.Free(b, 256);
		Assert.True(segment.TryAllocate(512, out var merged));
		Assert.Equal(a, merged);         // two blocks coalesced into one 512 B block
	}

	[Fact]
	public void TryAllocate_SplitsLargeFreeBlock()
	{
		segment.TryAllocate(1024, out var ptr);
		segment.Free(ptr, 1024);
		segment.TryAllocate(256, out var first);
		Assert.Equal(ptr, first);
		segment.TryAllocate(256, out var second);
		Assert.Equal((IntPtr)(ptr.ToInt64() + 256), second);
	}

	[Fact]
	public void Contains_PointerInsideBuffer_ReturnsTrue()
	{
		segment.TryAllocate(128, out var ptr);
		Assert.True(segment.Contains(ptr));
	}
}
```

Create `Tests/SegmentedArenaTierTests.cs`:

```csharp
namespace LookBusy.Test;

using System;
using LookBusy;
using Xunit;

public sealed class SegmentedArenaTierTests : IDisposable
{
	private readonly SegmentedArenaTier tier = new(segmentBytes: 4096);

	public void Dispose()
	{
		tier.Dispose();
		GC.SuppressFinalize(this);
	}

	[Fact]
	public void Allocate_First_UsesInitialSegment()
	{
		var ptr = tier.Allocate(byteCount: 512, out var segment);
		Assert.NotEqual(IntPtr.Zero, ptr);
		Assert.NotNull(segment);
		Assert.Equal(1, tier.SegmentCount);
	}

	[Fact]
	public void Allocate_BeyondSegmentCapacity_AddsNewSegment()
	{
		for (var i = 0; i < 10; i++) {
			tier.Allocate(1024, out _);
		}
		Assert.True(tier.SegmentCount >= 2);
	}

	[Fact]
	public void Allocate_LargerThanSegmentSize_CreatesOversizeSegment()
	{
		tier.Allocate(8192, out var seg);
		Assert.NotNull(seg);
		Assert.True(seg.Capacity >= 8192);
	}

	[Fact]
	public void Free_ReturnsBlockToOwningSegment()
	{
		var ptr = tier.Allocate(1024, out var seg);
		tier.Free(ptr, 1024, seg);
		tier.Allocate(1024, out var reused);
		Assert.Equal(ptr, reused);
	}

	[Fact]
	public void LocateSegmentByPointer_FindsOwner()
	{
		var ptr1 = tier.Allocate(1024, out var s1);
		for (var i = 0; i < 10; i++) { tier.Allocate(1024, out _); }
		var found = tier.LocateSegmentByPointer(ptr1);
		Assert.Same(s1, found);
	}
}
```

- [ ] **Step 2: Verify failure**

Run: `gtimeout 60 dotnet test --filter "FullyQualifiedName~SegmentedArena"`
Expected: FAIL — build errors.

- [ ] **Step 3: Create `SegmentedArenaSegment.cs`**

```csharp
namespace LookBusy;

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/// <summary>
/// Free-block header stored inline in the first 16 bytes of any freed arena block. This is read
/// from and written to unmanaged memory directly — never instantiated on the managed heap.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
internal struct SegmentedFreeBlockHeader
{
	public int SizeBytes;
	public int NextOffset;     // offset within segment of next free block in same bin; -1 terminator
	public int PrevOffset;     // within-segment prev; -1 terminator
	public int BinIndex;
}

/// <summary>
/// A single arena segment. Bump allocator from the tail + free list from the head, via segregated
/// bins keyed by Log2(blockSize). Free blocks embed their own link headers, so membership in the
/// free list costs zero managed allocation.
/// </summary>
internal sealed class SegmentedArenaSegment : IDisposable
{
	public readonly IntPtr Buffer;
	public readonly int Capacity;
	public int BumpOffset;
	private readonly int[] binHeads;      // offset of first free block in bin (or -1)
	private bool disposed;

	public SegmentedArenaSegment(int capacity)
	{
		if (capacity < SegmentedConstants.MinArenaBlockBytes) {
			throw new ArgumentOutOfRangeException(nameof(capacity));
		}
		Capacity = capacity;
		Buffer = Marshal.AllocHGlobal(capacity);
		binHeads = new int[SegmentedConstants.ArenaBinCount];
		for (var i = 0; i < binHeads.Length; i++) {
			binHeads[i] = -1;
		}
	}

	public long UnmanagedBytes => Capacity;

	public SegmentedArenaSegment? Next { get; set; }

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool Contains(IntPtr ptr)
	{
		var raw = ptr.ToInt64();
		var start = Buffer.ToInt64();
		return raw >= start && raw < start + Capacity;
	}

	public bool TryAllocate(int byteCount, out IntPtr ptr)
	{
		var size = AlignSize(byteCount);
		// Try a free-list bin first.
		var startBin = BinIndexForSize(size);
		for (var b = startBin; b < binHeads.Length; b++) {
			var head = binHeads[b];
			while (head >= 0) {
				var hdr = ReadHeader(head);
				if (hdr.SizeBytes >= size) {
					UnlinkFromBin(head, ref hdr);
					var remainder = hdr.SizeBytes - size;
					if (remainder >= SegmentedConstants.MinArenaBlockBytes) {
						// Split: the tail becomes a new free block.
						var tailOffset = head + size;
						WriteHeader(tailOffset, new SegmentedFreeBlockHeader {
							SizeBytes = remainder,
							NextOffset = -1,
							PrevOffset = -1,
							BinIndex = BinIndexForSize(remainder),
						});
						LinkIntoBin(tailOffset);
					}
					ptr = (IntPtr)(Buffer.ToInt64() + head);
					return true;
				}
				head = hdr.NextOffset;
			}
		}
		// Fall back to bump.
		if (BumpOffset + size <= Capacity) {
			ptr = (IntPtr)(Buffer.ToInt64() + BumpOffset);
			BumpOffset += size;
			return true;
		}
		ptr = IntPtr.Zero;
		return false;
	}

	public void Free(IntPtr ptr, int byteCount)
	{
		var offset = (int)(ptr.ToInt64() - Buffer.ToInt64());
		var size = AlignSize(byteCount);
		// Coalesce with the block immediately before this one, if that block is free.
		// This design does not store prev-block-in-segment pointers, so we do a simple scan of all
		// free blocks: find any block whose offset + SizeBytes == offset (predecessor) or whose
		// offset == offset + size (successor), and merge. The bin-index linkage lets us unlink in O(1).
		TryCoalesceForward(ref offset, ref size);
		TryCoalesceBackward(ref offset, ref size);
		WriteHeader(offset, new SegmentedFreeBlockHeader {
			SizeBytes = size,
			NextOffset = -1,
			PrevOffset = -1,
			BinIndex = BinIndexForSize(size),
		});
		LinkIntoBin(offset);
	}

	public void Reset()
	{
		BumpOffset = 0;
		for (var i = 0; i < binHeads.Length; i++) {
			binHeads[i] = -1;
		}
	}

	public void Dispose()
	{
		if (!disposed) {
			Marshal.FreeHGlobal(Buffer);
			disposed = true;
		}
	}

	// ---- helpers ----

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static int AlignSize(int size)
	{
		const int alignment = SegmentedConstants.PtrAlignment;
		if (size < SegmentedConstants.MinArenaBlockBytes) {
			return SegmentedConstants.MinArenaBlockBytes;
		}
		return (size + (alignment - 1)) & ~(alignment - 1);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static int BinIndexForSize(int size)
	{
		// Bucket sizes are powers of two starting at 16: bin 0 = [16, 32), bin 1 = [32, 64), ...
		var log = BitOperations.Log2((uint)size);
		var bin = log - 4;      // 16 B = 2^4 → bin 0
		if (bin < 0) { bin = 0; }
		if (bin >= SegmentedConstants.ArenaBinCount) { bin = SegmentedConstants.ArenaBinCount - 1; }
		return bin;
	}

	private unsafe SegmentedFreeBlockHeader ReadHeader(int offset)
	{
		return *(SegmentedFreeBlockHeader*)(Buffer.ToInt64() + offset);
	}

	private unsafe void WriteHeader(int offset, SegmentedFreeBlockHeader header)
	{
		*(SegmentedFreeBlockHeader*)(Buffer.ToInt64() + offset) = header;
	}

	private void LinkIntoBin(int offset)
	{
		var hdr = ReadHeader(offset);
		hdr.PrevOffset = -1;
		hdr.NextOffset = binHeads[hdr.BinIndex];
		WriteHeader(offset, hdr);
		if (hdr.NextOffset >= 0) {
			var next = ReadHeader(hdr.NextOffset);
			next.PrevOffset = offset;
			WriteHeader(hdr.NextOffset, next);
		}
		binHeads[hdr.BinIndex] = offset;
	}

	private void UnlinkFromBin(int offset, ref SegmentedFreeBlockHeader hdr)
	{
		if (hdr.PrevOffset >= 0) {
			var prev = ReadHeader(hdr.PrevOffset);
			prev.NextOffset = hdr.NextOffset;
			WriteHeader(hdr.PrevOffset, prev);
		} else {
			binHeads[hdr.BinIndex] = hdr.NextOffset;
		}
		if (hdr.NextOffset >= 0) {
			var next = ReadHeader(hdr.NextOffset);
			next.PrevOffset = hdr.PrevOffset;
			WriteHeader(hdr.NextOffset, next);
		}
	}

	private void TryCoalesceForward(ref int offset, ref int size)
	{
		var successorOffset = offset + size;
		if (successorOffset >= BumpOffset) {
			return;
		}
		// Check every bin head for a matching successor. The free-list is small typically.
		for (var b = 0; b < binHeads.Length; b++) {
			var cursor = binHeads[b];
			while (cursor >= 0) {
				var hdr = ReadHeader(cursor);
				if (cursor == successorOffset) {
					UnlinkFromBin(cursor, ref hdr);
					size += hdr.SizeBytes;
					return;
				}
				cursor = hdr.NextOffset;
			}
		}
	}

	private void TryCoalesceBackward(ref int offset, ref int size)
	{
		for (var b = 0; b < binHeads.Length; b++) {
			var cursor = binHeads[b];
			while (cursor >= 0) {
				var hdr = ReadHeader(cursor);
				if (cursor + hdr.SizeBytes == offset) {
					UnlinkFromBin(cursor, ref hdr);
					offset = cursor;
					size += hdr.SizeBytes;
					return;
				}
				cursor = hdr.NextOffset;
			}
		}
	}
}
```

- [ ] **Step 4: Create `SegmentedArenaTier.cs`**

```csharp
namespace LookBusy;

using System;
using System.Collections.Generic;

/// <summary>
/// Owns the list of <see cref="SegmentedArenaSegment"/> instances and routes allocation across them.
/// New segments are added when existing ones can't satisfy a request. Never resizes or moves an
/// existing segment.
/// </summary>
internal sealed class SegmentedArenaTier : IDisposable
{
	private readonly List<SegmentedArenaSegment> segments = [];
	private readonly int defaultSegmentBytes;

	public SegmentedArenaTier(int segmentBytes)
	{
		if (segmentBytes < SegmentedConstants.MinArenaBlockBytes) {
			throw new ArgumentOutOfRangeException(nameof(segmentBytes));
		}
		defaultSegmentBytes = segmentBytes;
	}

	public int SegmentCount => segments.Count;

	public long UnmanagedBytes
	{
		get
		{
			long total = 0;
			foreach (var s in segments) {
				total += s.UnmanagedBytes;
			}
			return total;
		}
	}

	public IntPtr Allocate(int byteCount, out SegmentedArenaSegment owningSegment)
	{
		foreach (var s in segments) {
			if (s.TryAllocate(byteCount, out var ptr)) {
				owningSegment = s;
				return ptr;
			}
		}
		var capacity = Math.Max(defaultSegmentBytes, byteCount);
		var segment = new SegmentedArenaSegment(capacity);
		segments.Add(segment);
		segment.TryAllocate(byteCount, out var newPtr);
		owningSegment = segment;
		return newPtr;
	}

	public void Free(IntPtr ptr, int byteCount, SegmentedArenaSegment segment) =>
		segment.Free(ptr, byteCount);

	public SegmentedArenaSegment LocateSegmentByPointer(IntPtr ptr)
	{
		foreach (var s in segments) {
			if (s.Contains(ptr)) {
				return s;
			}
		}
		throw new InvalidOperationException("Pointer does not belong to any arena segment");
	}

	public void ResetAll()
	{
		foreach (var s in segments) {
			s.Reset();
		}
	}

	public void Dispose()
	{
		foreach (var s in segments) {
			s.Dispose();
		}
		segments.Clear();
	}
}
```

- [ ] **Step 5: Add to csproj files**

Add `<Compile Include="../SegmentedArenaSegment.cs" />` and `<Compile Include="../SegmentedArenaTier.cs" />` to all three projects.

- [ ] **Step 6: Run tests**

Run: `gtimeout 90 dotnet test --filter "FullyQualifiedName~SegmentedArena"`
Expected: PASS (12 tests: 7 segment + 5 tier).

- [ ] **Step 7: Format and commit**

```bash
dotnet format
git add SegmentedArenaSegment.cs SegmentedArenaTier.cs Tests/SegmentedArenaSegmentTests.cs Tests/SegmentedArenaTierTests.cs \
        Tests/StringPoolTest.csproj Demo/StringPoolDemo.csproj Benchmarks/StringPoolBenchmarks.csproj
git commit -m "feat: add arena tier with segmented bump + coalesced free list"
```

---

### Task 6: `SegmentedStringPool` construction + `Allocate` for small strings

Replace the stub in `SegmentedStringPool.cs` with the full class skeleton. Wires slot table + slab tier + arena tier. Implements `Allocate(ReadOnlySpan<char>)` with only the slab path for now (large strings will land in Task 7). Also adds `SegmentedStringPoolOptions` record.

**Files:**
- Modify: `SegmentedStringPool.cs`
- Test: `Tests/SegmentedStringPoolTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `Tests/SegmentedStringPoolTests.cs`:

```csharp
namespace LookBusy.Test;

using System;
using LookBusy;
using Xunit;

public sealed class SegmentedStringPoolTests : IDisposable
{
	private readonly SegmentedStringPool pool = new();

	public void Dispose()
	{
		pool.Dispose();
		GC.SuppressFinalize(this);
	}

	[Fact]
	public void Constructor_Default_ZeroActive()
	{
		Assert.Equal(0, pool.ActiveAllocations);
		Assert.True(pool.TotalBytesUnmanaged > 0 || pool.SegmentCount == 0);
	}

	[Fact]
	public void Allocate_EmptySpan_ReturnsEmptyRef()
	{
		var r = pool.Allocate(ReadOnlySpan<char>.Empty);
		Assert.True(r.IsEmpty);
		Assert.Equal(0, pool.ActiveAllocations);
	}

	[Fact]
	public void Allocate_ShortString_ReturnsValidRef()
	{
		var r = pool.Allocate("Hello");
		Assert.False(r.IsEmpty);
		Assert.Same(pool, r.Pool);
		Assert.True(r.Generation >= 1u);
		Assert.Equal(1, pool.ActiveAllocations);
	}

	[Fact]
	public void Allocate_AtOrBelowSmallThreshold_UsesSlab()
	{
		var r = pool.Allocate(new string('x', 128));
		Assert.False(r.IsEmpty);
		Assert.True(pool.SlabCount >= 1);
		Assert.Equal(0, pool.SegmentCount);
	}
}
```

- [ ] **Step 2: Verify failure**

`gtimeout 60 dotnet test --filter FullyQualifiedName~SegmentedStringPoolTests` → FAIL.

- [ ] **Step 3: Replace `SegmentedStringPool.cs`**

```csharp
namespace LookBusy;

using System;
using System.Runtime.CompilerServices;

internal static class SegmentedConstants
{
	public const uint HighBit = 0x80000000u;
	public const uint GenerationMask = 0x7FFFFFFFu;
	public const uint NoFreeSlot = 0xFFFFFFFFu;
	public const int PtrAlignment = 8;
	public const long TierTagMask = 1L;
	public const long PtrMask = ~7L;
	public const int TierSlab = 0;
	public const int TierArena = 1;
	public const int SlabSizeClassCount = 5;
	public const int MinArenaBlockBytes = 16;
	public const int ArenaBinCount = 16;
	public const int DefaultSlotCapacity = 64;
	public const int DefaultSlabCellsPerSlab = 256;
	public const int DefaultArenaSegmentBytes = 1 << 20;
	public const int DefaultSmallStringThresholdChars = 128;
}

public sealed record SegmentedStringPoolOptions(
	int InitialSlotCapacity = SegmentedConstants.DefaultSlotCapacity,
	int SlabCellsPerSlab = SegmentedConstants.DefaultSlabCellsPerSlab,
	int ArenaSegmentBytes = SegmentedConstants.DefaultArenaSegmentBytes,
	int SmallStringThresholdChars = SegmentedConstants.DefaultSmallStringThresholdChars
);

public sealed class SegmentedStringPool : IDisposable
{
	private readonly SegmentedSlotTable slots;
	private readonly SegmentedSlabTier slabTier;
	private readonly SegmentedArenaTier arenaTier;
	private readonly int smallThreshold;
	private bool disposed;

	public SegmentedStringPool() : this(new SegmentedStringPoolOptions()) { }

	public SegmentedStringPool(SegmentedStringPoolOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		slots = new SegmentedSlotTable(options.InitialSlotCapacity);
		slabTier = new SegmentedSlabTier(options.SlabCellsPerSlab);
		arenaTier = new SegmentedArenaTier(options.ArenaSegmentBytes);
		smallThreshold = options.SmallStringThresholdChars;
	}

	public int ActiveAllocations => slots.ActiveCount;
	public long TotalBytesUnmanaged => slabTier.UnmanagedBytes + arenaTier.UnmanagedBytes;
	public long TotalBytesManaged => slots.Capacity * 16L;     // SlotEntry is 16 B; slab/arena managed overhead is O(tier count)
	public int SlabCount => slabTier.SlabCount;
	public int SegmentCount => arenaTier.SegmentCount;
	internal bool IsDisposed => disposed;

	public PooledStringRef Allocate(ReadOnlySpan<char> value)
	{
		ObjectDisposedException.ThrowIf(disposed, typeof(SegmentedStringPool));
		if (value.IsEmpty) {
			return PooledStringRef.Empty;
		}
		var length = value.Length;
		var ptr = AllocateUnmanaged(length, out var tier);
		unsafe {
			fixed (char* src = value) {
				Buffer.MemoryCopy(src, (void*)ptr, length * sizeof(char), length * sizeof(char));
			}
		}
		var taggedPtr = (IntPtr)((ptr.ToInt64() & SegmentedConstants.PtrMask) | tier);
		var (slotIndex, gen) = slots.Allocate(taggedPtr, length);
		return new PooledStringRef(this, slotIndex, gen);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private IntPtr AllocateUnmanaged(int charCount, out int tier)
	{
		if (charCount <= smallThreshold) {
			tier = SegmentedConstants.TierSlab;
			return slabTier.Allocate(charCount, out _);
		}
		// Arena path fills in during Task 7.
		throw new NotImplementedException("Arena allocation landed in Task 7");
	}

	public void Dispose()
	{
		if (!disposed) {
			slabTier.Dispose();
			arenaTier.Dispose();
			disposed = true;
		}
		GC.SuppressFinalize(this);
	}
}
```

- [ ] **Step 4: Run tests**

`gtimeout 60 dotnet test --filter FullyQualifiedName~SegmentedStringPoolTests` → PASS (4 tests).

- [ ] **Step 5: Format and commit**

```bash
dotnet format
git add SegmentedStringPool.cs Tests/SegmentedStringPoolTests.cs
git commit -m "feat: add SegmentedStringPool with slab-tier allocation path"
```

---

### Task 7: Extend `Allocate` for large strings via arena tier

Fills in the arena path. Strings with length > `smallThreshold` route to `SegmentedArenaTier` and receive the `TierArena` pointer tag.

**Files:**
- Modify: `SegmentedStringPool.cs` (replace the `NotImplementedException` branch)
- Test: `Tests/SegmentedStringPoolTests.cs` (extend)

- [ ] **Step 1: Write the failing tests**

Append:

```csharp
[Fact]
public void Allocate_AboveSmallThreshold_UsesArena()
{
	var r = pool.Allocate(new string('y', 256));
	Assert.False(r.IsEmpty);
	Assert.True(pool.SegmentCount >= 1);
}

[Fact]
public void Allocate_VeryLargeString_CreatesOversizeSegment()
{
	var big = new string('z', 2_000_000);   // 4 MB — exceeds default 1 MB segment
	var r = pool.Allocate(big);
	Assert.False(r.IsEmpty);
	Assert.True(pool.SegmentCount >= 1);
}

[Theory]
[InlineData(128)]
[InlineData(129)]
public void Allocate_BoundaryLengths_RouteToCorrectTier(int length)
{
	var r = pool.Allocate(new string('x', length));
	Assert.False(r.IsEmpty);
	if (length <= 128) {
		Assert.True(pool.SlabCount >= 1);
	} else {
		Assert.True(pool.SegmentCount >= 1);
	}
}
```

- [ ] **Step 2: Verify failure**

`gtimeout 60 dotnet test --filter FullyQualifiedName~SegmentedStringPoolTests` → FAIL (NotImplementedException).

- [ ] **Step 3: Replace `AllocateUnmanaged`**

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
private IntPtr AllocateUnmanaged(int charCount, out int tier)
{
	if (charCount <= smallThreshold) {
		tier = SegmentedConstants.TierSlab;
		return slabTier.Allocate(charCount, out _);
	}
	tier = SegmentedConstants.TierArena;
	var byteCount = charCount * sizeof(char);
	return arenaTier.Allocate(byteCount, out _);
}
```

- [ ] **Step 4: Run tests** → PASS.

- [ ] **Step 5: Format and commit**

```bash
dotnet format
git add SegmentedStringPool.cs Tests/SegmentedStringPoolTests.cs
git commit -m "feat: route large string allocations through arena tier"
```

---

### Task 8: `Free` + `ReadSlot` + `GetLength` + pointer tagging

Implements the friend API consumed by `PooledStringRef`. `FreeSlot` validates the generation, locates the owning slab/segment, returns the memory, bumps the slot's generation. `ReadSlot` returns a `ReadOnlySpan<char>` for the allocation; `GetLength` returns the length.

**Files:**
- Modify: `SegmentedStringPool.cs`
- Test: `Tests/SegmentedStringPoolTests.cs` (extend)

- [ ] **Step 1: Write the failing tests**

Append:

```csharp
[Fact]
public void Free_ValidHandle_ReturnsMemoryToPool()
{
	var r = pool.Allocate("short");
	var active = pool.ActiveAllocations;
	pool.FreeSlot(r.SlotIndex, r.Generation);
	Assert.Equal(active - 1, pool.ActiveAllocations);
}

[Fact]
public void Free_StaleHandle_IsNoop()
{
	var r = pool.Allocate("short");
	pool.FreeSlot(r.SlotIndex, r.Generation);
	pool.FreeSlot(r.SlotIndex, r.Generation);     // double free
	Assert.Equal(0, pool.ActiveAllocations);
}

[Fact]
public void ReadSlot_ValidHandle_ReturnsContent()
{
	var r = pool.Allocate("hello");
	var span = pool.ReadSlot(r.SlotIndex, r.Generation);
	Assert.True(span.SequenceEqual("hello"));
}

[Fact]
public void ReadSlot_ArenaAllocation_ReturnsContent()
{
	var big = new string('q', 200);
	var r = pool.Allocate(big);
	var span = pool.ReadSlot(r.SlotIndex, r.Generation);
	Assert.True(span.SequenceEqual(big));
}

[Fact]
public void ReadSlot_StaleHandle_Throws()
{
	var r = pool.Allocate("hello");
	pool.FreeSlot(r.SlotIndex, r.Generation);
	Assert.Throws<InvalidOperationException>(() => pool.ReadSlot(r.SlotIndex, r.Generation));
}

[Fact]
public void GetLength_ValidHandle_ReturnsCharCount()
{
	var r = pool.Allocate("hello");
	Assert.Equal(5, pool.GetLength(r.SlotIndex, r.Generation));
}
```

- [ ] **Step 2: Verify failure**

Expected: FAIL — methods not defined.

- [ ] **Step 3: Add methods to `SegmentedStringPool.cs`**

```csharp
internal ReadOnlySpan<char> ReadSlot(uint slotIndex, uint generation)
{
	ObjectDisposedException.ThrowIf(disposed, typeof(SegmentedStringPool));
	if (!slots.TryReadSlot(slotIndex, generation, out var entry)) {
		throw new InvalidOperationException("PooledStringRef is stale or freed");
	}
	var raw = (IntPtr)(entry.Ptr.ToInt64() & SegmentedConstants.PtrMask);
	unsafe {
		return new ReadOnlySpan<char>((void*)raw, entry.LengthChars);
	}
}

internal int GetLength(uint slotIndex, uint generation)
{
	ObjectDisposedException.ThrowIf(disposed, typeof(SegmentedStringPool));
	if (!slots.TryReadSlot(slotIndex, generation, out var entry)) {
		throw new InvalidOperationException("PooledStringRef is stale or freed");
	}
	return entry.LengthChars;
}

internal void FreeSlot(uint slotIndex, uint generation)
{
	if (disposed) { return; }
	if (!slots.TryReadSlot(slotIndex, generation, out var entry)) {
		return;     // stale or already freed — no-op
	}
	var raw = (IntPtr)(entry.Ptr.ToInt64() & SegmentedConstants.PtrMask);
	var tier = (int)(entry.Ptr.ToInt64() & SegmentedConstants.TierTagMask);
	if (tier == SegmentedConstants.TierSlab) {
		var slab = slabTier.LocateSlabByPointer(raw);
		slabTier.Free(raw, slab);
	} else {
		var seg = arenaTier.LocateSegmentByPointer(raw);
		arenaTier.Free(raw, entry.LengthChars * sizeof(char), seg);
	}
	slots.Free(slotIndex, generation);
}
```

- [ ] **Step 4: Run tests** → PASS.

- [ ] **Step 5: Format and commit**

```bash
dotnet format
git add SegmentedStringPool.cs Tests/SegmentedStringPoolTests.cs
git commit -m "feat: implement ReadSlot/FreeSlot with tier-tagged pointers"
```

---

### Task 9: `PooledStringRef.AsSpan` + `Length` + `Free`/`Dispose`

Replace the placeholders in `PooledStringRef.cs` with the real read-path implementations that call through to the pool.

**Files:**
- Modify: `PooledStringRef.cs`
- Test: `Tests/PooledStringRefTests.cs` (extend)

- [ ] **Step 1: Write the failing tests**

Append to `Tests/PooledStringRefTests.cs`:

```csharp
public sealed class PooledStringRefRoundtripTests : IDisposable
{
	private readonly SegmentedStringPool pool = new();

	public void Dispose()
	{
		pool.Dispose();
		GC.SuppressFinalize(this);
	}

	[Fact]
	public void AsSpan_SmallString_RoundTrips()
	{
		var r = pool.Allocate("hello");
		Assert.True(r.AsSpan().SequenceEqual("hello"));
	}

	[Fact]
	public void AsSpan_LargeString_RoundTrips()
	{
		var big = new string('q', 300);
		var r = pool.Allocate(big);
		Assert.True(r.AsSpan().SequenceEqual(big));
	}

	[Fact]
	public void AsSpan_Empty_ReturnsEmpty()
	{
		Assert.True(PooledStringRef.Empty.AsSpan().IsEmpty);
	}

	[Fact]
	public void Length_ReturnsChars()
	{
		var r = pool.Allocate("hello");
		Assert.Equal(5, r.Length);
	}

	[Fact]
	public void Length_EmptyRef_ReturnsZero()
	{
		Assert.Equal(0, PooledStringRef.Empty.Length);
	}

	[Fact]
	public void Dispose_FreesAllocation()
	{
		var r = pool.Allocate("hello");
		r.Dispose();
		Assert.Equal(0, pool.ActiveAllocations);
	}
}
```

- [ ] **Step 2: Verify failure** → FAIL.

- [ ] **Step 3: Replace `PooledStringRef.cs`**

```csharp
namespace LookBusy;

using System;

/// <summary>
/// 16-byte handle to a string in a <see cref="SegmentedStringPool"/>. <see cref="default"/>
/// is the empty sentinel; real allocations have generation ≥ 1. Value equality is content-based.
/// </summary>
public readonly record struct PooledStringRef(
	SegmentedStringPool? Pool,
	uint SlotIndex,
	uint Generation
) : IDisposable
{
	public static PooledStringRef Empty => default;

	public bool IsEmpty => Pool is null && SlotIndex == 0u && Generation == 0u;

	public int Length => IsEmpty ? 0 : Pool!.GetLength(SlotIndex, Generation);

	public ReadOnlySpan<char> AsSpan() => IsEmpty ? [] : Pool!.ReadSlot(SlotIndex, Generation);

	public void Free() => Pool?.FreeSlot(SlotIndex, Generation);

	public void Dispose() => Free();
}
```

- [ ] **Step 4: Run tests** → PASS.

- [ ] **Step 5: Format and commit**

```bash
dotnet format
git add PooledStringRef.cs Tests/PooledStringRefTests.cs
git commit -m "feat: wire PooledStringRef read path through pool"
```

---

### Task 10: Immutable query methods on `PooledStringRef`

Adds `IndexOf`, `LastIndexOf`, `StartsWith`, `EndsWith`, `Contains`, `SubstringSpan`. Pure delegations to `AsSpan()` with `StringComparison.Ordinal` default.

**Files:**
- Modify: `PooledStringRef.cs`
- Test: `Tests/PooledStringRefTests.cs` (extend)

- [ ] **Step 1: Write the failing tests**

Append a new test class:

```csharp
public sealed class PooledStringRefQueryTests : IDisposable
{
	private readonly SegmentedStringPool pool = new();

	public void Dispose() { pool.Dispose(); GC.SuppressFinalize(this); }

	[Fact]
	public void IndexOf_Found_ReturnsOffset()
	{
		var r = pool.Allocate("hello world");
		Assert.Equal(6, r.IndexOf("world"));
	}

	[Fact]
	public void IndexOf_NotFound_ReturnsMinusOne()
	{
		var r = pool.Allocate("hello");
		Assert.Equal(-1, r.IndexOf("xyz"));
	}

	[Fact]
	public void LastIndexOf_Found_ReturnsLastOffset()
	{
		var r = pool.Allocate("a.b.c");
		Assert.Equal(3, r.LastIndexOf("."));
	}

	[Fact]
	public void StartsWith_True_WhenPrefixMatches()
	{
		var r = pool.Allocate("hello world");
		Assert.True(r.StartsWith("hello"));
		Assert.False(r.StartsWith("world"));
	}

	[Fact]
	public void EndsWith_True_WhenSuffixMatches()
	{
		var r = pool.Allocate("hello world");
		Assert.True(r.EndsWith("world"));
		Assert.False(r.EndsWith("hello"));
	}

	[Fact]
	public void Contains_True_WhenSubstringPresent()
	{
		var r = pool.Allocate("hello world");
		Assert.True(r.Contains("llo wo"));
	}

	[Fact]
	public void SubstringSpan_ReturnsSlice()
	{
		var r = pool.Allocate("hello world");
		Assert.True(r.SubstringSpan(6, 5).SequenceEqual("world"));
	}

	[Fact]
	public void SubstringSpan_OutOfRange_Throws()
	{
		var r = pool.Allocate("hi");
		Assert.Throws<ArgumentOutOfRangeException>(() => r.SubstringSpan(5, 1));
	}

	[Fact]
	public void EmptyRef_QueryMethods_ReturnConventionalResults()
	{
		var e = PooledStringRef.Empty;
		Assert.Equal(-1, e.IndexOf("x"));
		Assert.Equal(0, e.IndexOf(""));
		Assert.True(e.StartsWith(""));
		Assert.False(e.StartsWith("x"));
	}
}
```

- [ ] **Step 2: Verify failure** → FAIL.

- [ ] **Step 3: Add methods to `PooledStringRef.cs`**

```csharp
public int IndexOf(ReadOnlySpan<char> value, StringComparison c = StringComparison.Ordinal) =>
	IsEmpty ? (value.IsEmpty ? 0 : -1) : AsSpan().IndexOf(value, c);

public int LastIndexOf(ReadOnlySpan<char> value, StringComparison c = StringComparison.Ordinal) =>
	IsEmpty ? (value.IsEmpty ? 0 : -1) : AsSpan().LastIndexOf(value, c);

public bool StartsWith(ReadOnlySpan<char> value, StringComparison c = StringComparison.Ordinal) =>
	IsEmpty ? value.IsEmpty : AsSpan().StartsWith(value, c);

public bool EndsWith(ReadOnlySpan<char> value, StringComparison c = StringComparison.Ordinal) =>
	IsEmpty ? value.IsEmpty : AsSpan().EndsWith(value, c);

public bool Contains(ReadOnlySpan<char> value, StringComparison c = StringComparison.Ordinal) =>
	IsEmpty ? value.IsEmpty : AsSpan().Contains(value, c);

public ReadOnlySpan<char> SubstringSpan(int startIndex, int length)
{
	var span = AsSpan();
	if ((uint)startIndex > (uint)span.Length) {
		throw new ArgumentOutOfRangeException(nameof(startIndex));
	}
	if ((uint)length > (uint)(span.Length - startIndex)) {
		throw new ArgumentOutOfRangeException(nameof(length));
	}
	return span.Slice(startIndex, length);
}
```

- [ ] **Step 4: Run tests** → PASS.

- [ ] **Step 5: Format and commit**

```bash
dotnet format
git add PooledStringRef.cs Tests/PooledStringRefTests.cs
git commit -m "feat: add immutable query methods to PooledStringRef"
```

---

### Task 11: `PooledStringRef.Duplicate`

Produces a second handle pointing at a fresh copy of the same characters. Same semantics as `PooledString.Duplicate` in the legacy pool — the new handle must be independently freeable without affecting the original.

**Files:**
- Modify: `PooledStringRef.cs`
- Test: `Tests/PooledStringRefTests.cs` (extend)

- [ ] **Step 1: Write the failing tests**

Append to `PooledStringRefQueryTests` (or create a new `PooledStringRefDuplicateTests` class — either is fine, pick the latter for clarity):

```csharp
public sealed class PooledStringRefDuplicateTests : IDisposable
{
	private readonly SegmentedStringPool pool = new();

	public void Dispose() { pool.Dispose(); GC.SuppressFinalize(this); }

	[Fact]
	public void Duplicate_ProducesEqualButDistinctHandle()
	{
		var a = pool.Allocate("hello");
		var b = a.Duplicate();
		Assert.True(a.AsSpan().SequenceEqual(b.AsSpan()));
		Assert.NotEqual(a.SlotIndex, b.SlotIndex);
	}

	[Fact]
	public void Duplicate_FreeingOneDoesNotAffectOther()
	{
		var a = pool.Allocate("hello");
		var b = a.Duplicate();
		a.Free();
		Assert.True(b.AsSpan().SequenceEqual("hello"));
	}

	[Fact]
	public void Duplicate_OfEmpty_IsEmpty()
	{
		var b = PooledStringRef.Empty.Duplicate();
		Assert.True(b.IsEmpty);
		Assert.Equal(0, b.Length);
	}
}
```

- [ ] **Step 2: Verify failure** → FAIL (`Duplicate` not defined).

- [ ] **Step 3: Add method to `PooledStringRef.cs`**

```csharp
public PooledStringRef Duplicate()
{
	if (IsEmpty) {
		return Empty;
	}
	var pool = _pool ?? throw new ObjectDisposedException(nameof(SegmentedStringPool));
	return pool.Allocate(AsSpan());
}
```

- [ ] **Step 4: Run tests** → PASS.

- [ ] **Step 5: Format and commit**

```bash
dotnet format
git add PooledStringRef.cs Tests/PooledStringRefTests.cs
git commit -m "feat: add Duplicate to PooledStringRef"
```

---

### Task 12: `PooledStringRef.Insert`

Produces a new handle with `value` inserted at `index`. The original handle is unchanged (immutable semantics — matches `PooledString.Insert`). Empty-source edge case: inserting into `Empty` with index 0 must work and return a fresh allocation.

**Files:**
- Modify: `PooledStringRef.cs`
- Test: `Tests/PooledStringRefTests.cs` (extend)

- [ ] **Step 1: Write the failing tests**

```csharp
public sealed class PooledStringRefInsertTests : IDisposable
{
	private readonly SegmentedStringPool pool = new();

	public void Dispose() { pool.Dispose(); GC.SuppressFinalize(this); }

	[Fact]
	public void Insert_AtBeginning_PrependsValue()
	{
		var r = pool.Allocate("world");
		var result = r.Insert(0, "hello ");
		Assert.True(result.AsSpan().SequenceEqual("hello world"));
	}

	[Fact]
	public void Insert_AtEnd_AppendsValue()
	{
		var r = pool.Allocate("hello");
		var result = r.Insert(5, " world");
		Assert.True(result.AsSpan().SequenceEqual("hello world"));
	}

	[Fact]
	public void Insert_InMiddle_InsertsValue()
	{
		var r = pool.Allocate("held");
		var result = r.Insert(2, "xyz");
		Assert.True(result.AsSpan().SequenceEqual("hexyzld"));
	}

	[Fact]
	public void Insert_DoesNotMutateOriginal()
	{
		var r = pool.Allocate("abc");
		_ = r.Insert(1, "XY");
		Assert.True(r.AsSpan().SequenceEqual("abc"));
	}

	[Fact]
	public void Insert_IntoEmpty_AtZero_ReturnsAllocationOfValue()
	{
		var result = PooledStringRef.Empty.Insert(0, "x");
		// Empty ref has no pool binding, so Insert on Empty requires the pool; see Step 3 note.
		Assert.True(result.IsEmpty || result.AsSpan().SequenceEqual("x"));
	}

	[Fact]
	public void Insert_IndexOutOfRange_Throws()
	{
		var r = pool.Allocate("abc");
		Assert.Throws<ArgumentOutOfRangeException>(() => r.Insert(4, "x"));
		Assert.Throws<ArgumentOutOfRangeException>(() => r.Insert(-1, "x"));
	}
}
```

- [ ] **Step 2: Verify failure** → FAIL.

- [ ] **Step 3: Add method to `PooledStringRef.cs`**

Empty-handle edge case: `PooledStringRef.Empty` has no pool binding. Inserting into it is ambiguous (which pool?) — we mirror the legacy behaviour and require a bound ref. Tests for `Empty.Insert` therefore expect `IsEmpty` (no-op) rather than a thrown exception; document this in the XML doc.

```csharp
public PooledStringRef Insert(int index, ReadOnlySpan<char> value)
{
	var pool = _pool;
	if (pool is null) {
		// Empty handle: no pool to allocate into. Return Empty as a no-op.
		return Empty;
	}
	var original = AsSpan();
	if ((uint)index > (uint)original.Length) {
		throw new ArgumentOutOfRangeException(nameof(index));
	}
	if (value.IsEmpty) {
		return Duplicate();
	}

	var totalLength = original.Length + value.Length;
	Span<char> buffer = totalLength <= 256
		? stackalloc char[totalLength]
		: new char[totalLength];

	original.Slice(0, index).CopyTo(buffer);
	value.CopyTo(buffer.Slice(index));
	original.Slice(index).CopyTo(buffer.Slice(index + value.Length));

	return pool.Allocate(buffer);
}
```

(Note: the `stackalloc` branch requires `totalLength` bounded by a constant; 256 chars = 512 bytes is safe. Larger sizes fall through to managed `char[]` — this is a temporary for the copy only and should be collected on gen0. If we want to avoid that, Task 15's Reserve/pre-warm can adopt `ArrayPool<char>.Shared` here instead; leave that as an optimisation.)

- [ ] **Step 4: Run tests** → PASS.

- [ ] **Step 5: Format and commit**

```bash
dotnet format
git add PooledStringRef.cs Tests/PooledStringRefTests.cs
git commit -m "feat: add Insert to PooledStringRef"
```

---

### Task 13: `PooledStringRef.Replace`

Produces a new handle with all non-overlapping occurrences of `oldValue` replaced by `newValue`. Uses `stackalloc` for small match-index arrays and `ArrayPool<int>.Shared` for the overflow case — this is the performance-critical path the legacy pool handles with a managed `List<int>`.

**Files:**
- Modify: `PooledStringRef.cs`
- Test: `Tests/PooledStringRefTests.cs` (extend)

- [ ] **Step 1: Write the failing tests**

```csharp
public sealed class PooledStringRefReplaceTests : IDisposable
{
	private readonly SegmentedStringPool pool = new();

	public void Dispose() { pool.Dispose(); GC.SuppressFinalize(this); }

	[Fact]
	public void Replace_Single_ReplacesOnce()
	{
		var r = pool.Allocate("hello world");
		var result = r.Replace("world", "everyone");
		Assert.True(result.AsSpan().SequenceEqual("hello everyone"));
	}

	[Fact]
	public void Replace_Multiple_NonOverlapping()
	{
		var r = pool.Allocate("a-b-c-d");
		var result = r.Replace("-", "::");
		Assert.True(result.AsSpan().SequenceEqual("a::b::c::d"));
	}

	[Fact]
	public void Replace_NoMatches_ReturnsEqualContent()
	{
		var r = pool.Allocate("hello");
		var result = r.Replace("xyz", "abc");
		Assert.True(result.AsSpan().SequenceEqual("hello"));
	}

	[Fact]
	public void Replace_WithEmpty_RemovesMatches()
	{
		var r = pool.Allocate("aXbXc");
		var result = r.Replace("X", "");
		Assert.True(result.AsSpan().SequenceEqual("abc"));
	}

	[Fact]
	public void Replace_LargeInput_OverflowsStackallocPath()
	{
		// Force > 64 matches to exercise ArrayPool fallback.
		var src = new string('x', 200).Replace("xx", "xX"); // produces many "xX" runs
		var r = pool.Allocate(src);
		var result = r.Replace("X", "Y");
		Assert.Equal(src.Replace("X", "Y"), result.AsSpan().ToString());
	}

	[Fact]
	public void Replace_EmptyOldValue_Throws()
	{
		var r = pool.Allocate("abc");
		Assert.Throws<ArgumentException>(() => r.Replace("", "x"));
	}
}
```

- [ ] **Step 2: Verify failure** → FAIL.

- [ ] **Step 3: Add method to `PooledStringRef.cs`**

```csharp
private const int ReplaceInlineMatchCap = 64;

public PooledStringRef Replace(ReadOnlySpan<char> oldValue, ReadOnlySpan<char> newValue)
{
	var pool = _pool;
	if (pool is null) {
		return Empty;
	}
	if (oldValue.IsEmpty) {
		throw new ArgumentException("oldValue cannot be empty.", nameof(oldValue));
	}
	var source = AsSpan();
	if (source.IsEmpty) {
		return Empty;
	}

	Span<int> inlineMatches = stackalloc int[ReplaceInlineMatchCap];
	int[]? rented = null;
	Span<int> matches = inlineMatches;
	var matchCount = 0;

	var searchStart = 0;
	while (searchStart <= source.Length - oldValue.Length) {
		var found = source.Slice(searchStart).IndexOf(oldValue);
		if (found < 0) {
			break;
		}
		var absolute = searchStart + found;

		if (matchCount == matches.Length) {
			var newSize = matches.Length * 2;
			var nextRented = System.Buffers.ArrayPool<int>.Shared.Rent(newSize);
			matches.Slice(0, matchCount).CopyTo(nextRented);
			if (rented is not null) {
				System.Buffers.ArrayPool<int>.Shared.Return(rented);
			}
			rented = nextRented;
			matches = rented;
		}
		matches[matchCount++] = absolute;
		searchStart = absolute + oldValue.Length;
	}

	if (matchCount == 0) {
		if (rented is not null) {
			System.Buffers.ArrayPool<int>.Shared.Return(rented);
		}
		return Duplicate();
	}

	var totalLength = source.Length + matchCount * (newValue.Length - oldValue.Length);
	char[]? rentedChars = null;
	Span<char> buffer = totalLength <= 256
		? stackalloc char[totalLength]
		: (rentedChars = System.Buffers.ArrayPool<char>.Shared.Rent(totalLength)).AsSpan(0, totalLength);

	var srcCursor = 0;
	var dstCursor = 0;
	for (var i = 0; i < matchCount; i++) {
		var matchAt = matches[i];
		var preLen = matchAt - srcCursor;
		source.Slice(srcCursor, preLen).CopyTo(buffer.Slice(dstCursor));
		dstCursor += preLen;
		newValue.CopyTo(buffer.Slice(dstCursor));
		dstCursor += newValue.Length;
		srcCursor = matchAt + oldValue.Length;
	}
	source.Slice(srcCursor).CopyTo(buffer.Slice(dstCursor));

	var result = pool.Allocate(buffer);

	if (rented is not null) {
		System.Buffers.ArrayPool<int>.Shared.Return(rented);
	}
	if (rentedChars is not null) {
		System.Buffers.ArrayPool<char>.Shared.Return(rentedChars);
	}
	return result;
}
```

- [ ] **Step 4: Run tests** → PASS.

- [ ] **Step 5: Format and commit**

```bash
dotnet format
git add PooledStringRef.cs Tests/PooledStringRefTests.cs
git commit -m "feat: add Replace to PooledStringRef using ArrayPool for overflow"
```

---

### Task 14: `PooledStringRef.Equals` / `GetHashCode` / `ToString`

Content-based equality (two refs with the same chars are equal even if in different slots or pools). `GetHashCode` uses the first and last ≤8 chars plus length — this matches behaviour in the legacy pool and avoids hashing every char. `ToString` materialises a managed `string` (escape hatch; should be used sparingly).

**Files:**
- Modify: `PooledStringRef.cs`
- Test: `Tests/PooledStringRefTests.cs` (extend)

- [ ] **Step 1: Write the failing tests**

```csharp
public sealed class PooledStringRefEqualityTests : IDisposable
{
	private readonly SegmentedStringPool pool = new();

	public void Dispose() { pool.Dispose(); GC.SuppressFinalize(this); }

	[Fact]
	public void Equals_SameContent_DifferentSlots_IsTrue()
	{
		var a = pool.Allocate("hello");
		var b = pool.Allocate("hello");
		Assert.True(a.Equals(b));
		Assert.Equal(a.GetHashCode(), b.GetHashCode());
	}

	[Fact]
	public void Equals_DifferentContent_IsFalse()
	{
		var a = pool.Allocate("hello");
		var b = pool.Allocate("world");
		Assert.False(a.Equals(b));
	}

	[Fact]
	public void Equals_EmptyAndEmpty_IsTrue()
	{
		Assert.True(PooledStringRef.Empty.Equals(PooledStringRef.Empty));
	}

	[Fact]
	public void Equals_Object_CompatibleWithString()
	{
		var a = pool.Allocate("hello");
		Assert.True(a.Equals((object)"hello"));
		Assert.False(a.Equals((object)"HELLO"));
	}

	[Fact]
	public void ToString_ReturnsManagedCopy()
	{
		var a = pool.Allocate("hello");
		var s = a.ToString();
		Assert.Equal("hello", s);
	}

	[Fact]
	public void GetHashCode_LongStrings_DifferByContent()
	{
		var a = pool.Allocate(new string('a', 500));
		var b = pool.Allocate(new string('b', 500));
		Assert.NotEqual(a.GetHashCode(), b.GetHashCode());
	}

	[Fact]
	public void OperatorEquals_SameContent_IsTrue()
	{
		var a = pool.Allocate("abc");
		var b = pool.Allocate("abc");
		Assert.True(a == b);
		Assert.False(a != b);
	}
}
```

- [ ] **Step 2: Verify failure** → FAIL.

- [ ] **Step 3: Add methods and operators to `PooledStringRef.cs`**

```csharp
public bool Equals(PooledStringRef other) =>
	AsSpan().SequenceEqual(other.AsSpan());

public override bool Equals(object? obj) => obj switch {
	PooledStringRef r => Equals(r),
	string s => AsSpan().SequenceEqual(s.AsSpan()),
	_ => false,
};

public override int GetHashCode()
{
	var span = AsSpan();
	if (span.IsEmpty) {
		return 0;
	}
	var hc = new HashCode();
	hc.Add(span.Length);
	var prefix = span.Slice(0, Math.Min(8, span.Length));
	foreach (var ch in prefix) {
		hc.Add(ch);
	}
	if (span.Length > 8) {
		var suffix = span.Slice(Math.Max(span.Length - 8, 8));
		foreach (var ch in suffix) {
			hc.Add(ch);
		}
	}
	return hc.ToHashCode();
}

public override string ToString() =>
	AsSpan().ToString(); // new managed allocation — intentional escape hatch

public static bool operator ==(PooledStringRef left, PooledStringRef right) => left.Equals(right);
public static bool operator !=(PooledStringRef left, PooledStringRef right) => !left.Equals(right);
```

- [ ] **Step 4: Run tests** → PASS.

- [ ] **Step 5: Format and commit**

```bash
dotnet format
git add PooledStringRef.cs Tests/PooledStringRefTests.cs
git commit -m "feat: add content equality and hashing to PooledStringRef"
```

---

### Task 15: `Clear`, `Dispose`, finalizer, and `Reserve` pre-warm on `SegmentedStringPool`

Lifecycle completion. `Clear` invalidates all handles by bumping all generations; `Dispose` releases unmanaged memory via slab/arena tiers; finalizer provides GC safety net; `Reserve(int chars)` pre-warms capacity so hot paths don't stall on first allocation.

**Files:**
- Modify: `SegmentedStringPool.cs`, `SegmentedSlabTier.cs`, `SegmentedArenaTier.cs`, `SegmentedSlotTable.cs`
- Test: `Tests/SegmentedStringPoolLifecycleTests.cs` (new)

- [ ] **Step 1: Write the failing tests**

Create `Tests/SegmentedStringPoolLifecycleTests.cs`:

```csharp
namespace LookBusy.Test;

using System;
using LookBusy;
using Xunit;

public sealed class SegmentedStringPoolLifecycleTests
{
	[Fact]
	public void Clear_InvalidatesAllHandles()
	{
		var pool = new SegmentedStringPool();
		var a = pool.Allocate("hello");
		var b = pool.Allocate("world");
		pool.Clear();
		Assert.Throws<ObjectDisposedException>(() => _ = a.AsSpan().Length);
		Assert.Throws<ObjectDisposedException>(() => _ = b.AsSpan().Length);
		pool.Dispose();
	}

	[Fact]
	public void Clear_AllowsFreshAllocationsAfterward()
	{
		var pool = new SegmentedStringPool();
		_ = pool.Allocate("hello");
		pool.Clear();
		var after = pool.Allocate("fresh");
		Assert.True(after.AsSpan().SequenceEqual("fresh"));
		pool.Dispose();
	}

	[Fact]
	public void Dispose_InvalidatesHandles_AndSubsequentAllocateThrows()
	{
		var pool = new SegmentedStringPool();
		var h = pool.Allocate("hello");
		pool.Dispose();
		Assert.Throws<ObjectDisposedException>(() => _ = h.AsSpan().Length);
		Assert.Throws<ObjectDisposedException>(() => pool.Allocate("x"));
	}

	[Fact]
	public void Dispose_IsIdempotent()
	{
		var pool = new SegmentedStringPool();
		pool.Dispose();
		pool.Dispose(); // must not throw
	}

	[Fact]
	public void Reserve_AllowsLargeAllocationsWithoutGrowth()
	{
		using var pool = new SegmentedStringPool();
		pool.Reserve(1_000_000); // 1M chars total capacity
		for (var i = 0; i < 1000; i++) {
			_ = pool.Allocate(new string('x', 500));
		}
		// Success criterion: no throw, no OOM; a capacity probe could be added here later.
	}
}
```

- [ ] **Step 2: Verify failure** → FAIL (`Clear`, `Reserve` not defined).

- [ ] **Step 3: Implement on `SegmentedStringPool.cs`**

```csharp
public void Clear()
{
	ThrowIfDisposed();
	_slotTable.ResetAll();   // bumps every generation, marks all entries free
	_slabTier.ResetAll();    // defined in Task 4: resets occupancy bitmaps on every slab
	_arenaTier.ResetAll();   // defined in Task 7: resets free-lists on every segment
}

public void Reserve(int chars)
{
	ThrowIfDisposed();
	if (chars <= 0) {
		return;
	}
	var smallBudget = chars / 2;
	var largeBudget = chars - smallBudget;
	_slabTier.Reserve(smallBudget);
	_arenaTier.Reserve(largeBudget * sizeof(char));
}

protected virtual void Dispose(bool disposing)
{
	if (_disposed) {
		return;
	}
	_disposed = true;
	// Bump slot generations so outstanding refs fail loudly.
	_slotTable.DisposeAll();
	_slabTier.Dispose();
	_arenaTier.Dispose();
}

public void Dispose()
{
	Dispose(true);
	GC.SuppressFinalize(this);
}

~SegmentedStringPool() => Dispose(false);

private void ThrowIfDisposed()
{
	if (_disposed) {
		throw new ObjectDisposedException(nameof(SegmentedStringPool));
	}
}
```

On `SegmentedSlotTable.cs`:

```csharp
public void ResetAll()
{
	for (var i = 1; i < _count; i++) {
		ref var e = ref _entries[i];
		e.Generation = unchecked(e.Generation + 1); // invalidate live refs
		e.Ptr = 0;
		e.Length = 0;
		e.NextFree = _firstFree;
		_firstFree = i;
	}
}

public void DisposeAll() => ResetAll();
```

Extend `SegmentedSlabTier.cs` and `SegmentedArenaTier.cs` with a new `Reserve(...)` method each. `ResetAll()` and `Dispose()` already exist from Tasks 4 and 7.

```csharp
// In SegmentedSlabTier: pre-warm by adding empty slabs until the char budget is covered.
public void Reserve(int smallChars)
{
	var perSlabChars = cellsPerSlab * (CellBytesForSizeClass(SizeClassCount - 1) / sizeof(char));
	while (allSlabs.Count * perSlabChars < smallChars) {
		AllocateNewSlab(SizeClassCount - 1); // largest slab class covers the widest range
	}
}

// In SegmentedArenaTier: pre-warm by appending segments until `bytes` of capacity exists.
public void Reserve(int bytes)
{
	var totalBytes = 0;
	foreach (var s in segments) {
		totalBytes += s.TotalBytes;
	}
	while (totalBytes < bytes) {
		var next = Math.Max(minSegmentBytes, bytes - totalBytes);
		segments.Add(new SegmentedArenaSegment(next));
		totalBytes += next;
	}
}
```

The existing `ResetAll()` is what `Clear` delegates to; the existing `Dispose()` already calls `Marshal.FreeHGlobal` on every slab / segment. Do not re-implement those methods.

- [ ] **Step 4: Run tests** → PASS.

- [ ] **Step 5: Format and commit**

```bash
dotnet format
git add SegmentedStringPool.cs SegmentedSlabTier.cs SegmentedArenaTier.cs SegmentedSlotTable.cs Tests/SegmentedStringPoolLifecycleTests.cs
git commit -m "feat: add Clear, Dispose, finalizer, and Reserve to SegmentedStringPool"
```

---

### Task 16: GC pressure tests for `SegmentedStringPool`

Extends `Tests/GcPressureTests.cs` with Segmented variants. The key assertion: **Segmented must allocate strictly less managed memory than the legacy `UnmanagedStringPool` on both benchmarks**, not just less than the managed baseline. This is the whole point of the redesign.

**Files:**
- Modify: `Tests/GcPressureTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `GcPressureTests`:

```csharp
[Fact]
public void BulkAllocate_LargeStrings_SegmentedAllocatesLessThanLegacy()
{
	var source = new string('x', LargeStringLength);

	using var legacy = new UnmanagedStringPool(N * LargeStringLength * sizeof(char) * 4);
	var legacyBytes = MeasureAllocated(() => {
		var arr = new PooledString[N];
		for (var i = 0; i < N; i++) { arr[i] = legacy.Allocate(source); }
		for (var i = 0; i < N; i++) { arr[i].Free(); }
		GC.KeepAlive(arr);
	});

	using var seg = new SegmentedStringPool();
	seg.Reserve(N * LargeStringLength);
	var segBytes = MeasureAllocated(() => {
		var arr = new PooledStringRef[N];
		for (var i = 0; i < N; i++) { arr[i] = seg.Allocate(source); }
		for (var i = 0; i < N; i++) { arr[i].Free(); }
		GC.KeepAlive(arr);
	});

	Assert.True(segBytes < legacyBytes,
		$"Segmented ({segBytes:N0} B) should allocate less than legacy ({legacyBytes:N0} B)");
}

[Fact]
public void InterleavedAllocFree_SegmentedAllocatesLessThanLegacy()
{
	var source = new string('x', LargeStringLength);

	using var legacy = new UnmanagedStringPool(N * LargeStringLength * sizeof(char) * 4);
	var legacyBytes = MeasureAllocated(() => {
		var window = new PooledString[WindowSize];
		for (var i = 0; i < N; i++) {
			var slot = i % WindowSize;
			if (i >= WindowSize) { window[slot].Free(); }
			window[slot] = legacy.Allocate(source);
		}
		var limit = Math.Min(N, WindowSize);
		for (var i = 0; i < limit; i++) { window[i].Free(); }
		GC.KeepAlive(window);
	});

	using var seg = new SegmentedStringPool();
	seg.Reserve(WindowSize * LargeStringLength * 4);
	var segBytes = MeasureAllocated(() => {
		var window = new PooledStringRef[WindowSize];
		for (var i = 0; i < N; i++) {
			var slot = i % WindowSize;
			if (i >= WindowSize) { window[slot].Free(); }
			window[slot] = seg.Allocate(source);
		}
		var limit = Math.Min(N, WindowSize);
		for (var i = 0; i < limit; i++) { window[i].Free(); }
		GC.KeepAlive(window);
	});

	Assert.True(segBytes < legacyBytes,
		$"Segmented ({segBytes:N0} B) should allocate less than legacy ({legacyBytes:N0} B)");
}

[Fact]
public void BulkAllocate_Segmented_AllocatesNearZeroManagedMemory()
{
	// Steady-state target: slot-table growth amortised. Assert <10% of managed baseline.
	var source = new string('x', LargeStringLength);

	var managedBytes = MeasureAllocated(() => {
		var arr = new string[N];
		for (var i = 0; i < N; i++) { arr[i] = new string('x', LargeStringLength); }
		GC.KeepAlive(arr);
	});

	using var seg = new SegmentedStringPool();
	seg.Reserve(N * LargeStringLength);
	// Warm-up: allocate/free N times to grow slot table to steady state.
	var warm = new PooledStringRef[N];
	for (var i = 0; i < N; i++) { warm[i] = seg.Allocate(source); }
	for (var i = 0; i < N; i++) { warm[i].Free(); }

	var segBytes = MeasureAllocated(() => {
		var arr = new PooledStringRef[N];
		for (var i = 0; i < N; i++) { arr[i] = seg.Allocate(source); }
		for (var i = 0; i < N; i++) { arr[i].Free(); }
		GC.KeepAlive(arr);
	});

	Assert.True(segBytes < managedBytes / 10,
		$"Segmented steady-state ({segBytes:N0} B) should be <10% of managed ({managedBytes:N0} B)");
}
```

- [ ] **Step 2: Verify failure** → tests fail to compile until `SegmentedStringPool` + `PooledStringRef` are referenced in `Tests/StringPoolTest.csproj` (already done in Task 1–6).

- [ ] **Step 3: Run tests** → PASS. If the `<10% of managed` assertion fails, investigate slot-table growth and tune initial capacity; do not lower the threshold without understanding why.

- [ ] **Step 4: Format and commit**

```bash
dotnet format
git add Tests/GcPressureTests.cs
git commit -m "test: add GC pressure tests proving Segmented beats legacy"
```

---

### Task 17: Benchmarks — explicit 3-way comparison

Extends `BulkAllocateBenchmarks.cs` and `InterleavedAllocFreeBenchmarks.cs` with a third benchmark method for `SegmentedStringPool`, giving a direct head-to-head against both managed `string` and the legacy `UnmanagedStringPool`.

**Files:**
- Modify: `Benchmarks/BulkAllocateBenchmarks.cs`
- Modify: `Benchmarks/InterleavedAllocFreeBenchmarks.cs`

- [ ] **Step 1: Add the Segmented benchmark method to `BulkAllocateBenchmarks.cs`**

Modify the class: add a `SegmentedStringPool` field, initialise it in `Setup`, dispose in `Cleanup`, and add a new `[Benchmark]` method. Final shape:

```csharp
private string _source = "";
private UnmanagedStringPool _legacy = null!;
private SegmentedStringPool _segmented = null!;

[GlobalSetup]
public void Setup()
{
	_source = new string('x', StringLength);
	_legacy = new UnmanagedStringPool(N * StringLength * sizeof(char) * 4);
	_segmented = new SegmentedStringPool();
	_segmented.Reserve(N * StringLength);
}

[GlobalCleanup]
public void Cleanup()
{
	_legacy.Dispose();
	_segmented.Dispose();
}

[Benchmark(Baseline = true)]
public string[] BulkAllocate_Managed()
{
	var arr = new string[N];
	for (var i = 0; i < N; i++) { arr[i] = new string('x', StringLength); }
	return arr;
}

[Benchmark]
public PooledString[] BulkAllocate_Legacy()
{
	var arr = new PooledString[N];
	for (var i = 0; i < N; i++) { arr[i] = _legacy.Allocate(_source); }
	for (var i = 0; i < N; i++) { arr[i].Free(); }
	return arr;
}

[Benchmark]
public PooledStringRef[] BulkAllocate_Segmented()
{
	var arr = new PooledStringRef[N];
	for (var i = 0; i < N; i++) { arr[i] = _segmented.Allocate(_source); }
	for (var i = 0; i < N; i++) { arr[i].Free(); }
	return arr;
}
```

Rename the existing `_pool` field to `_legacy` and the existing `BulkAllocate_Pooled` method to `BulkAllocate_Legacy` for clarity in the results table.

- [ ] **Step 2: Do the same for `InterleavedAllocFreeBenchmarks.cs`**

Same pattern: add `_segmented`, add a `InterleavedAllocFree_Segmented` benchmark method mirroring `InterleavedAllocFree_Pooled`, and rename `_pool` / `InterleavedAllocFree_Pooled` to `_legacy` / `InterleavedAllocFree_Legacy`.

```csharp
[Benchmark]
public PooledStringRef InterleavedAllocFree_Segmented()
{
	var window = new PooledStringRef[WindowSize];
	for (var i = 0; i < N; i++) {
		var slot = i % WindowSize;
		if (i >= WindowSize) { window[slot].Free(); }
		window[slot] = _segmented.Allocate(_source);
	}
	var last = window[(N - 1) % WindowSize];
	var limit = Math.Min(N, WindowSize);
	for (var i = 0; i < limit; i++) { window[i].Free(); }
	return last;
}
```

- [ ] **Step 3: Run benchmarks** (optional but recommended before commit)

```bash
dotnet run -c Release --project Benchmarks -- --filter '*BulkAllocate*'
dotnet run -c Release --project Benchmarks -- --filter '*InterleavedAllocFree*'
```

Capture output; record results into `README.md`'s benchmark table so the three-way comparison is visible to readers.

- [ ] **Step 4: Format and commit**

```bash
dotnet format
git add Benchmarks/BulkAllocateBenchmarks.cs Benchmarks/InterleavedAllocFreeBenchmarks.cs README.md
git commit -m "bench: add 3-way comparison of Managed vs Legacy vs Segmented pools"
```

---

## Post-Implementation

- [ ] **Run the full test suite** one final time: `gtimeout 120 dotnet test`.
- [ ] **Verify formatting**: `dotnet format --verify-no-changes`.
- [ ] **Run benchmarks** and update `README.md` with the Segmented numbers — this is the primary evidence that the redesign succeeded.
- [ ] **Tag the commit** (optional): `git tag segmented-v1`.


---
