# SegmentedStringPool — Design Spec

**Date:** 2026-04-17
**Status:** Draft (awaiting review)
**Scope:** Additional unmanaged string pool class, sibling to the existing `UnmanagedStringPool`. Reduces managed GC pressure by eliminating per-allocation `Dictionary`/`SortedList` bookkeeping, improves performance for both small and large strings via a tiered slab + arena allocator, and removes the need for in-place defragmentation via a multi-segment growth strategy.

---

## 1. Motivation

The existing `UnmanagedStringPool` keeps string data in unmanaged memory but still incurs significant managed GC pressure from its metadata:

1. `Dictionary<uint, AllocationInfo> allocations` — one entry per live string; grows, rehashes, and contributes ~30% of total managed heap per the current `GcPressureTests` (measured ~30% of equivalent managed-string workload).
2. `SortedList<int, List<FreeBlock>> freeBlocksBySize` — each size class holds a `List<FreeBlock>` whose backing array grows and allocates as free blocks churn.
3. `List<int>` inside `PooledString.Replace` for occurrence tracking.
4. `DefragmentAndGrowPool` rewrites every dictionary entry whenever the pool grows (O(n) managed writes).
5. Allocation IDs are monotonically increasing and never reused; 4 B cap means long-lived pools under churn can exhaust IDs.

Structural friction:
- No small-string optimisation: every string pays dictionary insert cost.
- `SortedList` binary search + `List<FreeBlock>` adds indirection to a hot path.

Goals for the new design:
- Zero managed allocation per string after a warm-up period.
- O(1) allocate and free for small strings; improved large-string paths.
- Safe ID reuse via generation counters — no exhaustion concern.
- No in-place defragmentation — growth appends new segments and leaves existing pointers valid.
- Feature parity with `PooledString`: `Insert`, `Replace`, `SubstringSpan`, span queries, value equality.

Non-goals:
- Drop-in replacement for `UnmanagedStringPool` — new type lives alongside it.
- Concurrent allocate/free without external synchronization (same threading model as existing pool).
- Cross-pool interop or string deduplication.

---

## 2. Architecture

**New class:** `SegmentedStringPool` (sibling to `UnmanagedStringPool`).
**New handle type:** `PooledStringRef` (16 bytes: pool reference + 32-bit slot index + 32-bit generation).

Three cooperating subsystems:

```
                ┌─────────────────────────────────────┐
                │       SegmentedStringPool           │
                │                                     │
 Allocate(span)→│  ┌──────────────────────────┐       │
                │  │    Slot Table            │       │  ← only managed
                │  │  SlotEntry[] (grows in   │          array that grows
                │  │   power-of-2 chunks)     │       │
                │  └──────────────────────────┘       │
                │           │                         │
                │           ▼ points into             │
                │  ┌──────────────┐ ┌──────────────┐  │
                │  │ Slab Tier    │ │ Arena Tier   │  │
                │  │ ≤128 chars   │ │ >128 chars   │  │
                │  │              │ │              │  │
                │  │ size classes │ │ segment list │  │
                │  │ 8/16/32/64/  │ │ + coalesced  │  │
                │  │ 128          │ │ free bins    │  │
                │  └──────────────┘ └──────────────┘  │
                │    unmanaged         unmanaged      │
                └─────────────────────────────────────┘
```

### 2.1 Slot Table

One managed `SlotEntry[]` indexed by slot index. Grows by doubling. Holds per-allocation metadata. Freed slots form an intrusive free-list (next-free-slot index stored in the pointer field of freed slots).

This is the only managed allocation whose size is proportional to live-string count.

### 2.2 Slab Tier

For strings ≤ 128 chars. One size-class chain per class (8, 16, 32, 64, 128 chars). Each slab is a fixed-size unmanaged buffer of equal-sized cells plus a managed bitmap. Allocation uses `BitOperations.TrailingZeroCount` for O(1) first-free-cell. Full slabs move off the active chain; freeing a cell in a full slab returns it to the active chain.

### 2.3 Arena Tier

For strings > 128 chars. List of unmanaged segments (default 1 MB each). Each segment maintains its own coalesced free list via segregated bins (power-of-2 size classes). New segment allocated when existing segments cannot satisfy a request. **No segment is ever resized or copied.**

---

## 3. Data Layout

### 3.1 Slot Table

```csharp
internal struct SlotEntry {
    // 16 bytes total, 8-byte aligned
    public IntPtr Ptr;          // unmanaged ptr to string data; low bit = tier tag
                                //   (or next-free-slot index when slot is free)
    public int LengthChars;     // string length in chars
    public uint Generation;     // bumped on every Free; high bit set = slot is free
}

private SlotEntry[] slots;
private int slotCount;          // high-water mark
private uint freeSlotHead;      // first free slot or uint.MaxValue
```

**Free-slot chaining:** when a slot is freed, its `Ptr` field stores the next-free-slot index and its `Generation` high bit is set. This avoids needing an additional free-slot list.

**Generation semantics:**
- `default(PooledStringRef)` has `Generation = 0` — reserved sentinel for the empty string.
- Real allocations: generation is incremented on **every** state transition (alloc → free and free → alloc). Initial allocation takes a fresh slot from gen `0` to gen `1`; a free/realloc cycle goes `1 → 2|HighBit → 3`. This gives strong stale-handle detection even across rapid alloc-free-alloc churn of the same slot.
- High bit of generation reserved to mark slot free; low 31 bits are the actual generation counter. When comparing against an outstanding handle's generation, the high bit is irrelevant because live handles always hold a generation with the high bit clear.

### 3.2 Slab Tier

```csharp
internal sealed class Slab {
    public readonly int CellBytes;       // aligned cell size (e.g. 16/32/64/128/256 bytes)
    public readonly int CellCount;       // e.g. 256 cells per slab
    public readonly IntPtr Buffer;       // CellBytes * CellCount, unmanaged
    public readonly ulong[] Bitmap;      // one bit per cell; 1 = free, 0 = used
    public int FreeCells;                // cached popcount
    public Slab? NextInClass;            // intrusive chain within size class
}
```

Size classes: 8, 16, 32, 64, 128 chars → cell sizes 16, 32, 64, 128, 256 bytes (char × 2, 8-byte aligned).

### 3.3 Arena Tier

```csharp
internal sealed class ArenaSegment {
    public readonly IntPtr Buffer;
    public readonly int Capacity;
    public int BumpOffset;                // next uninitialised byte
    public readonly int[] FreeBinHeads;   // 16 bins; offset of first free block per bin
    public ArenaSegment? Next;
}

// Stored inline in the first 16 bytes of every freed block:
internal struct FreeBlockHeader {
    public int SizeBytes;
    public int NextBlockOffset;           // intrusive free-list within segment
    public int PrevBlockOffset;           // for coalescing
    public int _reserved;
}
```

Free bins point to the head of an intrusive doubly-linked list stored in the freed blocks themselves — zero managed allocation for free-list membership.

**Minimum arena allocation size:** every arena allocation must be ≥ `sizeof(FreeBlockHeader)` (16 bytes, i.e. 8 chars) so that when it is later freed the block can hold the header. The `SmallStringThresholdChars = 128` boundary already guarantees this because any string routed to the arena is > 128 chars. Block splitting uses `MinSplitBytes = sizeof(FreeBlockHeader) = 16` — remainders smaller than this are consumed by the requester rather than being split off.

### 3.4 Handle Struct

```csharp
public readonly record struct PooledStringRef(
    SegmentedStringPool Pool,
    uint SlotIndex,
    uint Generation
) : IDisposable {
    public static PooledStringRef Empty => default;
}
```

`default(PooledStringRef)` is the empty sentinel. Every real allocation has generation ≥ 1.

### 3.5 Tier Tagging

The low bit of `SlotEntry.Ptr` encodes the tier:
- `0` → slab allocation
- `1` → arena allocation

Safe because all buffers are 8-byte aligned; low 3 bits of any pointer are always zero. On deref, mask with `~7`.

---

## 4. Allocation & Free Flow

### 4.1 Allocate(ReadOnlySpan\<char\>)

```
Allocate(span):
    if span.IsEmpty: return PooledStringRef.Empty
    charCount = span.Length
    byteCount = AlignSize(charCount * 2)

    if charCount ≤ SmallStringThresholdChars:
        sizeClass = ChooseSizeClass(charCount)   // O(1) via Log2
        slab = sizeClasses[sizeClass].Active
        if slab == null or slab.FreeCells == 0:
            slab = AllocateOrPromoteSlab(sizeClass)
        cellIndex = FindFreeBit(slab.Bitmap)     // TrailingZeroCount
        ClearBit(slab.Bitmap, cellIndex)
        slab.FreeCells -= 1
        ptr = slab.Buffer + cellIndex * slab.CellBytes
        ptr = TagSlab(ptr)
    else:
        ptr = AllocateFromArena(byteCount)       // see 4.2
        ptr = TagArena(ptr)

    MemoryCopy(span -> unmasked(ptr), byteCount)

    slotIndex = PopFreeSlot()                    // or extend slot table
    slot = slots[slotIndex]
    slot.Ptr = ptr
    slot.LengthChars = charCount
    slot.Generation = (slot.Generation + 1) & ~HighBit   // clear free-mark, bump gen
    return new PooledStringRef(this, slotIndex, slot.Generation)
```

### 4.2 Arena allocation

```
AllocateFromArena(byteCount):
    bin = Log2(byteCount)
    for each segment:
        for b = bin .. 15:
            if segment.FreeBinHeads[b] is not empty:
                block = PopFrontBin(segment, b)
                if block.SizeBytes >= byteCount + MinSplitBytes:
                    remainder = Split(block, byteCount)
                    PushFrontBin(segment, Log2(remainder.SizeBytes), remainder)
                return block.OffsetToPtr()
    for each segment:
        if segment.Capacity - segment.BumpOffset >= byteCount:
            ptr = segment.Buffer + segment.BumpOffset
            segment.BumpOffset += byteCount
            return ptr
    segment = AllocateNewSegment(max(SegmentSize, byteCount))
    segments.Add(segment)
    ptr = segment.Buffer
    segment.BumpOffset = byteCount
    return ptr
```

### 4.3 Free(ref)

```
Free(ref):
    if ref.IsEmpty: return
    if ref.SlotIndex >= slotCount: return
    slot = slots[ref.SlotIndex]
    if (slot.Generation & HighBit) != 0: return         // already free
    if slot.Generation != ref.Generation: return        // stale

    rawPtr = slot.Ptr & ~7
    tier = slot.Ptr & 1

    if tier == SLAB:
        slab = LocateSlab(rawPtr)                       // binary search slabs by address
        cellIndex = (rawPtr - slab.Buffer) / slab.CellBytes
        SetBit(slab.Bitmap, cellIndex)
        slab.FreeCells += 1
        PromoteSlabIfReactivated(slab)
    else:
        segment = LocateSegment(rawPtr)                 // binary search segments by address
        header = FreeBlockHeader at rawPtr
        header.SizeBytes = AlignSize(slot.LengthChars * 2)
        CoalesceWithNeighbours(segment, header)
        PushFrontBin(segment, Log2(header.SizeBytes), header)

    // Mark slot free and link into free-slot chain
    slot.Generation = (slot.Generation + 1) | HighBit
    slot.Ptr = (IntPtr)freeSlotHead
    freeSlotHead = ref.SlotIndex
```

Double-free is safe: the generation check fails silently on the second call.

### 4.4 Fast-path dereference

```
AsSpan(ref):
    if ref.SlotIndex == 0 and ref.Generation == 0: return []
    slot = slots[ref.SlotIndex]
    if slot.Generation != ref.Generation: throw InvalidOperationException
    rawPtr = slot.Ptr & ~7
    return new ReadOnlySpan<char>((void*)rawPtr, slot.LengthChars)
```

One array index, two int comparisons, one mask, one span constructor. No dictionary lookup.

### 4.5 Growth (no defragmentation)

- **Slot table:** double `slots[]` capacity when full. `Array.Copy`. Slot indices and ptrs remain valid.
- **Slab:** append new `Slab` to size class's chain. No effect on existing allocations.
- **Arena:** append new `ArenaSegment` to segments list. Existing segment pointers untouched.

**No operation in this design ever moves string data.** This is the key departure from `UnmanagedStringPool.DefragmentAndGrowPool`.

### 4.6 Clear()

Walks the slot table and increments every live slot's generation, marking it free and rebuilding the free-slot chain as a full chain over `[0..slotCount)`. Every slab's bitmap is reset to all-free. Every arena segment's `BumpOffset` resets to 0 and free bins are cleared. Slots array, slab chains, and segment list are retained (avoid thrashing).

All outstanding `PooledStringRef` handles become stale, matching `UnmanagedStringPool.Clear()` semantics.

---

## 5. Public API

### 5.1 `SegmentedStringPool`

```csharp
public sealed class SegmentedStringPool : IDisposable
{
    public SegmentedStringPool();
    public SegmentedStringPool(SegmentedStringPoolOptions options);

    public PooledStringRef Allocate(ReadOnlySpan<char> value);

    // Pre-warm: advisory hint to pre-allocate slabs/segments sized for the workload
    public void Reserve(int approximateAverageLengthChars, int approximateCount);

    public int ActiveAllocations { get; }
    public long TotalBytesUnmanaged { get; }
    public long TotalBytesManaged { get; }
    public int SlabCount { get; }
    public int SegmentCount { get; }

    public void Clear();
    public void Dispose();

    // Friend API (internal) consumed by PooledStringRef
    internal ReadOnlySpan<char> ReadSlot(uint slotIndex, uint generation);
    internal int GetLength(uint slotIndex, uint generation);
    internal void FreeSlot(uint slotIndex, uint generation);
    internal PooledStringRef AllocateUninitialised(int lengthChars);
    internal void WriteAtPosition(uint slotIndex, uint generation, int start, ReadOnlySpan<char> value);
}

public sealed record SegmentedStringPoolOptions(
    int InitialSlotCapacity = 64,
    int SlabCellsPerSlab = 256,
    int ArenaSegmentBytes = 1 << 20,         // 1 MB
    int SmallStringThresholdChars = 128
);
```

### 5.2 `PooledStringRef`

```csharp
public readonly record struct PooledStringRef(
    SegmentedStringPool Pool,
    uint SlotIndex,
    uint Generation
) : IDisposable
{
    public static PooledStringRef Empty => default;
    public bool IsEmpty { get; }
    public int Length { get; }

    public ReadOnlySpan<char> AsSpan();
    public void Free();
    public void Dispose();                   // calls Free

    public int IndexOf(ReadOnlySpan<char> value, StringComparison c = StringComparison.Ordinal);
    public int LastIndexOf(ReadOnlySpan<char> value, StringComparison c = StringComparison.Ordinal);
    public bool StartsWith(ReadOnlySpan<char> value, StringComparison c = StringComparison.Ordinal);
    public bool EndsWith(ReadOnlySpan<char> value, StringComparison c = StringComparison.Ordinal);
    public bool Contains(ReadOnlySpan<char> value, StringComparison c = StringComparison.Ordinal);
    public ReadOnlySpan<char> SubstringSpan(int startIndex, int length);

    public PooledStringRef Duplicate();
    public PooledStringRef Insert(int pos, ReadOnlySpan<char> value);
    public PooledStringRef Replace(ReadOnlySpan<char> oldValue, ReadOnlySpan<char> newValue);

    public bool Equals(PooledStringRef other);
    public override int GetHashCode();
    public override string ToString();
}
```

### 5.3 Notable departures from existing API

- No `initialCapacityChars` / `AllowGrowth` constructor args — capacity is elastic.
- No `DefragmentAndGrowPool` / `FragmentationPercentage` / `EndBlockSizeChars` — no single buffer, no defrag concept.
- Stale-handle access throws `InvalidOperationException` (not `ArgumentException` as the existing pool does) — the handle format is valid, the referent is gone.
- `Replace` implemented with `stackalloc int[64]` for the common path, `ArrayPool<int>.Shared` fallback — no `List<int>`.

### 5.4 Error handling

| Condition | Behaviour |
|---|---|
| Empty span to `Allocate` | Returns `PooledStringRef.Empty`, no pool interaction |
| Span length overflow when aligned | Throws `ArgumentOutOfRangeException` |
| Allocate on disposed pool | Throws `ObjectDisposedException` |
| `AsSpan` on stale/freed handle | Throws `InvalidOperationException` |
| `AsSpan` on disposed pool | Throws `ObjectDisposedException` |
| Double-`Free` | No-op |
| `Free` on `PooledStringRef.Empty` | No-op |
| Slot count exceeds `int.MaxValue` | Throws `OutOfMemoryException` |
| Native allocation failure | Propagates `OutOfMemoryException` |

### 5.5 Disposal

`Dispose` frees every slab buffer and every arena segment, nulls the slot table, sets `IsDisposed`. Finalizer provides safety net for abandoned pools (same pattern as existing pool).

---

## 6. Testing Strategy (Draft)

Mirrors the existing test structure; tests live under `Tests/` in `LookBusy.Test` namespace.

### 6.1 Unit tests (per-tier)

- **SlotTableTests** — allocate-free-reallocate, generation bumps, free-chain correctness, stale handle rejection, capacity doubling, `default(PooledStringRef)` as empty sentinel.
- **SlabTests** — bitmap bit-set/clear, first-free-cell location, full-slab promotion, chain walking when current slab full.
- **ArenaTests** — bump allocation, free-list pop, block splitting, coalescing of adjacent free blocks, bin selection at power-of-2 boundaries.

### 6.2 Integration tests (parity with existing `PooledString`)

Port the following suites from the existing pool:
- **SegmentedStringPoolTests** (mirrors `UnmanagedStringPoolTests`) — core alloc/free/read/length.
- **SegmentedStringPoolEdgeCaseTests** — zero-length, single-char, max-length strings; pool-disposal; stale-handle access.
- **PooledStringRefTests** — `Insert`, `Replace`, `SubstringSpan`, `IndexOf`/`LastIndexOf`/`StartsWith`/`EndsWith`/`Contains`, equality, hash code, `ToString`.
- **DisposalAndLifecycleTests** — pool disposal invalidates all refs; finaliser path.
- **CopyBehaviorTests** — struct copy semantics; freeing one copy invalidates all copies of that slot.
- **ClearMethodTests** — `Clear()` bumps all generations; all outstanding refs invalid; subsequent allocations succeed.
- **ConcurrentAccessTests** — concurrent reads safe, concurrent mutation requires external sync (matches existing contract).
- **IntegerOverflowTests** — overflow in length × sizeof(char), overflow at `uint.MaxValue` slots.

### 6.3 Tier-boundary tests

- Strings at `SmallStringThresholdChars` boundary route correctly (≤ 128 → slab, > 128 → arena).
- Re-allocating after freeing a slab cell reuses that cell.
- Re-allocating after freeing an arena block reuses a block of at least the right size class.
- Coalescing: free three adjacent arena blocks, confirm they merge into one.
- Cross-segment allocation when current segment full.

### 6.4 GC pressure tests (`GcPressureTests`)

Extend existing tests to include `SegmentedStringPool`:
- `BulkAllocate_Large_Segmented_AllocatesMuchLessThanExisting` — assert `SegmentedStringPool` managed bytes < 50% of `UnmanagedStringPool` managed bytes for the 10 000 × 256-char workload.
- `BulkAllocate_Small_Segmented_AllocatesMuchLessThanExisting` — same assertion for 10 000 × 8-char (slab path).
- `InterleavedAllocFree_SteadyState_ZeroAllocationsAfterWarmup` — after a warm-up phase fills the slot table/slabs/segments, subsequent steady-state alloc/free produces zero GC allocations (measured via `GC.GetAllocatedBytesForCurrentThread`).

### 6.5 Benchmarks

Extend `Benchmarks/BulkAllocateBenchmarks.cs` and `Benchmarks/InterleavedAllocFreeBenchmarks.cs` to compare:
- `BulkAllocate_Managed` (baseline, existing)
- `BulkAllocate_Pooled` (existing `UnmanagedStringPool`)
- `BulkAllocate_Segmented` (new)

Same `N ∈ {1000, 10000}` and `StringLength ∈ {8, 256}` parameter sweep. Report allocation, mean, and GC counts.

---

## 7. Implementation Notes

- Target framework: .NET 10 (matches existing `net10.0` project).
- Project layout: `SegmentedStringPool.cs` and `PooledStringRef.cs` at the project root alongside the existing types. Internal types (`SlotEntry`, `Slab`, `ArenaSegment`, `FreeBlockHeader`) as nested or file-scoped `internal` types.
- Follow existing code style: tabs, `file-scoped namespace`, braces on same line, analyzer rules inherited from `.editorconfig`.
- Run `dotnet format` after each change.
- All tests run with `gtimeout` per project convention.

---

## 8. Open Items

None at the time of writing — all architectural decisions have been made.

## 9. Decision Log

| Decision | Chosen | Rejected alternatives |
|---|---|---|
| Relationship to existing pool | Separate class, new handle type | Drop-in replacement; shared `PooledString` struct |
| Allocation identity | 64-bit handle: 32-bit slot + 32-bit generation | Inline headers; monotonic-ID array; 32-bit packed handle |
| Allocator structure | Slab (≤128 chars) + segmented arena (>128 chars) | Single arena with better free list; two-ended bump arena |
| Threading | External synchronization for mutations | Lock-free reads; fully concurrent |
| Growth / fragmentation | Multi-segment arena, no in-place defrag | Single buffer + compact API; automatic defrag |
| `Clear()` cost | Bump every slot's generation | Single epoch counter |
| Mutation API | Full `Insert` / `Replace` parity with `PooledString` | Read-only; overwrite-only |
| Pre-warm | `Reserve(avgLength, count)` advisory method | Lazy only; constructor-options flag |

