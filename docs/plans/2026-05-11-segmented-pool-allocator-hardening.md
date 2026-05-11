# Segmented Pool Allocator Hardening

Scope: `SegmentedStringPool`, `SegmentedArenaTier`, `SegmentedArenaSegment`, `SegmentedSlotEntry`, `SegmentedSlotTable`, and `PooledStringRef` only.

This plan intentionally excludes already-fixed historical review items and minor cleanup. Each item below is a correctness or robustness issue with a concrete failure mode.

Apply TDD: write the failing test first, then implement the fix.

---

## 1. Normalize arena allocation sizes before segment selection

### Problem

`SegmentedArenaTier.Allocate` creates a new segment with:

```csharp
var capacity = Math.Max(defaultSegmentBytes, byteCount);
```

`SegmentedArenaSegment.TryAllocate` then aligns the same request upward to 8 bytes before checking capacity. If an oversized request is not already 8-byte aligned, the new segment can be too small for the aligned allocation.

The call result is currently ignored:

```csharp
_ = segment.TryAllocate(byteCount, out var newPtr, out allocatedBytes);
```

So the tier can return `IntPtr.Zero`, and `SegmentedStringPool.Allocate` will copy into that pointer.

Default repro shape:

- `ArenaSegmentBytes = 1 << 20`
- allocate `524_289` chars
- raw byte count is `1_048_578`
- aligned byte count is `1_048_584`
- new segment capacity is only `1_048_578`

### Fix

Centralize arena size normalization:

- Add an internal helper that computes the aligned arena allocation size using checked arithmetic.
- Use the normalized size for oversized detection.
- Use the normalized size for new segment capacity.
- Keep `allocatedBytes` as the actual granted extent from `TryAllocate`.
- Do not ignore `TryAllocate` on a freshly created segment. If it fails, throw an explicit allocator-invariant exception.

Likely shape:

```csharp
internal static int NormalizeAllocationBytes(int byteCount)
```

or expose the existing `AlignSize` as an internal static method on `SegmentedArenaSegment`.

### Tests

Add tests before implementation:

- `SegmentedArenaTier.Allocate_OversizedUnalignedRequest_CreatesAlignedCapacitySegment`
- `SegmentedStringPool.Allocate_UnalignedOversizedArenaString_RoundTrips`
- `SegmentedArenaTier.Allocate_FreshSegmentFailure_DoesNotReturnZeroPointer`

The last test can use a small custom `ArenaSegmentBytes` to force boundary conditions without huge allocations.

---

## 2. Check all char-to-byte and alignment arithmetic

### Problem

Several conversions from character counts to byte counts are unchecked:

- `SegmentedStringPool.Allocate`: copy byte count is `length * sizeof(char)`
- `SegmentedStringPool.AllocateUnmanaged`: arena byte count is `charCount * sizeof(char)`
- `SegmentedStringPool.ReserveLarge`: reservation byte count is `chars * sizeof(char)`
- `SegmentedArenaSegment.AlignSize`: `(size + (alignment - 1)) & ~(alignment - 1)` can overflow
- `SegmentedArenaSegment.TryAllocate`: `BumpOffset + size <= Capacity` can overflow
- `SegmentedArenaTier.Reserve`: `totalBytes += s.Capacity` and `bytes - totalBytes` are unchecked

Most callers will never approach these limits, but the allocator accepts `int` lengths and sizes. Silent wraparound in unmanaged allocation code is not acceptable; the failure mode is under-allocation followed by unsafe copy or inconsistent allocator state.

### Fix

Make size conversion explicit and checked:

- Add named constants for arena alignment and maximum safe char counts.
- Convert char counts to byte counts with `checked`.
- Align with a checked helper.
- Use `long` for aggregate reservation totals where summing multiple segment capacities.
- Throw `ArgumentOutOfRangeException` for invalid caller-provided sizes.
- Throw `OverflowException` only for internal arithmetic overflow where the caller input was otherwise valid.

Preferred behavior:

- `ReserveLarge(int.MaxValue)` throws instead of wrapping negative.
- A too-large span allocation throws before unmanaged allocation or copy.
- `TryAllocate` cannot succeed or fail based on overflowed `BumpOffset + size`.

### Tests

Add tests before implementation:

- `SegmentedStringPool.ReserveLarge_ByteCountOverflow_ThrowsArgumentOutOfRangeException`
- `SegmentedStringPool.Allocate_ArenaByteCountOverflow_ThrowsBeforeCopy`
- `SegmentedArenaSegment.TryAllocate_AlignmentOverflow_Throws`
- `SegmentedArenaSegment.TryAllocate_BumpOffsetOverflow_DoesNotAllocate`
- `SegmentedArenaTier.Reserve_TotalBytesOverflow_Throws`

If constructing a real span near `int.MaxValue` is impractical, test the lower-level normalization helpers directly with internals-visible tests.

---

## 3. Define and enforce slot-generation exhaustion behavior

### Problem

`SegmentedSlotEntry.Generation` uses one high bit as the free flag and 31 bits as the reuse counter:

```csharp
((GenerationValue(generation) + 1u) & SegmentedConstants.GenerationMask)
```

This wraps. After enough free/reallocate transitions on the same slot, a stale `PooledStringRef` can become valid again if its old generation value reappears. The current comments describe the counter as monotonically increasing and stale handles as detected, but the implementation is eventually ABA-susceptible.

This is unlikely in normal workloads but it is a real correctness boundary for a handle type whose contract includes dangling-ref detection.

### Fix

Pick an explicit policy and implement it.

Recommended policy: retire exhausted slots.

- When bumping would wrap the 31-bit generation value, mark the slot permanently retired.
- Retired slots are not pushed onto the free list.
- Allocation skips retired slots and uses a fresh high-water slot.
- `ActiveCount` remains correct.
- Capacity growth remains unchanged.

Alternative policy: widen the generation.

- This likely widens `PooledStringRef` beyond the current 16-byte handle target unless the layout is redesigned.
- Only choose this if retaining slots forever is more important than preserving the handle size.

Do not leave the behavior implicit. If wraparound is accepted by design, document it as a hard contract limitation and add a test that captures the chosen behavior.

### Tests

Add tests before implementation:

- `SegmentedSlotEntry.MarkFreeAndBumpGen_AtMax_DoesNotWrapToReusableGeneration`
- `SegmentedSlotTable.Free_AtGenerationExhaustion_RetiresSlot`
- `SegmentedSlotTable.Allocate_AfterRetirement_DoesNotReuseRetiredSlot`
- `PooledStringRef_StaleHandle_DoesNotBecomeValidAfterGenerationExhaustion`

Use test-only helpers or `InternalsVisibleTo` access to seed a slot near `GenerationMask`; do not attempt billions of alloc/free cycles.

---

## Suggested Sequence

1. Fix arena size normalization first. It has the most direct memory-safety failure mode.
2. Add checked size arithmetic next. It closes the same class of under-allocation bugs across reserve and copy paths.
3. Decide and implement generation exhaustion behavior. It is less likely operationally, but it affects the public correctness contract for stale handles.

Before commit, run:

```bash
dotnet build
dotnet format
gtimeout 120 dotnet test
```

