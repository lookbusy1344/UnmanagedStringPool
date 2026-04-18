# SegmentedStringPool — Internals

A walk through the data structures behind `SegmentedStringPool`, focused on the
two mechanisms that do most of the work: **bitmask tricks** (pointer tagging,
generation flags, slab occupancy) and **linked lists** (slot free chain, slab
chains, arena free-block bins).

This document assumes you've read the design spec
(`docs/superpowers/specs/2026-04-17-segmented-string-pool-design.md`) and want
the implementation-level "how does it actually work" picture.

---

## 1. The big picture

A single allocation flows through three cooperating subsystems:

```
Allocate(span) → SlotTable → (SlabTier or ArenaTier) → unmanaged byte ptr
                    │             │             │
                managed array  bitmap+chain  bins+coalesce
```

- **`SegmentedSlotTable`** — managed array of 16-byte entries. The only managed
  storage that scales with the live-string count. Hands out `(slotIndex, generation)`
  pairs; consumers never see the raw pointer.
- **`SegmentedSlabTier`** — five fixed-size-class chains (8/16/32/64/128 chars).
  Each chain is a list of slabs; each slab is a bitmap over fixed-size cells.
- **`SegmentedArenaTier`** — list of large segments (default 1 MB each). Each
  segment is bump-allocated from the tail and a coalesced free-list at the head.

The `PooledStringRef` returned to the caller is `(pool, slotIndex, generation)`
— 16 bytes, fully copyable, with no direct pointer into unmanaged memory.

---

## 2. Bitmask tricks

### 2.1 Pointer tagging — tier in the low bit

`Marshal.AllocHGlobal` returns pointers aligned to at least 8 bytes on every
supported platform. That means the low **3 bits** of any pointer it returns are
always zero — free real estate.

The pool uses bit 0 to record which tier owns the allocation, so `FreeSlot`
knows whether to call back into the slab tier or the arena tier without
storing a separate field per slot.

```
raw_ptr      = 0x00007FFF_AABBCC00      (always aligned; low 3 bits = 000)
tagged_ptr   = (raw & ~7L) | tier_bit   (bit 0 = 0 slab, 1 arena)
```

Encode (`SegmentedStringPool.Allocate`):

```csharp
var taggedPtr = new IntPtr((ptr.ToInt64() & PtrMask) | (long)tier);
```

Decode (`SegmentedStringPool.FreeSlot`):

```csharp
var raw  = new IntPtr(entry.Ptr.ToInt64() & PtrMask);   // ~7L
var tier = (int)(entry.Ptr.ToInt64() & TierTagMask);    //  1L
```

The tagged pointer is what gets stored in the slot entry's `Ptr` field, so the
tier bit travels alongside the pointer and costs zero extra bytes.

### 2.2 Generation field — `[free flag | 31-bit counter]`

Each slot has a 32-bit generation number. The **high bit** marks whether the
slot is currently free; the low 31 bits are a wrap-around counter that bumps
on every state change.

```
generation = [F][cccccccc cccccccc cccccccc ccccccc]
              ^                                       ^
              bit 31 = 1 means freed                  bit 0
```

The two relevant constants:

```csharp
public const uint HighBit        = 0x80000000u;
public const uint GenerationMask = 0x7FFFFFFFu;
```

`MarkFreeAndBumpGen` and `ClearFreeAndBumpGen` both increment the counter
*and* flip the flag, so a stale `PooledStringRef` from a reused slot can never
match the new generation:

```csharp
public static uint MarkFreeAndBumpGen(uint generation) =>
    ((GenerationValue(generation) + 1u) & GenerationMask) | HighBit;

public static uint ClearFreeAndBumpGen(uint generation) =>
    (GenerationValue(generation) + 1u) & GenerationMask;
```

The counter wraps every 2³¹ reuses of the same slot, which is the only
theoretical collision risk; in practice you'd burn through all of unmanaged
memory long before it bites.

### 2.3 Slab bitmap — one bit per cell, `1 = free`

Each `SegmentedSlab` has a managed `ulong[] bitmap` covering its cells, with
the convention **1 = free, 0 = used**. This inversion exists for one reason:
`BitOperations.TrailingZeroCount(word)` finds the first set bit, which under
this convention finds the first **free** cell directly — no complement needed.
On x86 it lowers to a single `tzcnt` instruction.

```
bitmap[0] = ...11111110_11110110   (binary, low bits on the right)
                          ^^^ used cells: 0, 3
            tzcnt picks bit 1 → cellIndex = 1
            mark used:        word & ~(1UL << 1)
```

Allocation hot path:

```csharp
for (var w = 0; w < bitmap.Length; w++) {
    var word = bitmap[w];
    if (word != 0UL) {
        var bit = BitOperations.TrailingZeroCount(word);
        cellIndex = (w * 64) + bit;
        bitmap[w] = word & ~(1UL << bit);   // flip free → used
        --freeCells;
        return true;
    }
}
```

The slab is initialised with all bits set (`ulong.MaxValue`) and the trailing
"phantom" bits past `CellCount` are masked off so they're never picked.

### 2.4 Arena bin index — `Log2(size) − 4`

The arena keeps 16 free-list bins keyed by power-of-two size class. The bin
index is computed via `BitOperations.Log2`, with the −4 offset because the
minimum block size is 16 bytes (`Log2(16) = 4` → bin 0):

```csharp
var bin = BitOperations.Log2((uint)size) - 4;
```

So 16-byte blocks land in bin 0, 32-byte in bin 1, 64-byte in bin 2, … up to
bin 15 covering ≥ 524288-byte blocks. Allocation searches from the smallest
adequate bin upward; it never overshoots into a larger bin without first
exhausting smaller candidates.

---

## 3. Linked lists

Three different linked-list shapes show up. Each one is intrusive — the link
fields live inside the data they describe, never in a separate `LinkedListNode`.

### 3.1 Slot table — singly-linked free chain

When a slot is freed, its `Ptr` field is repurposed to hold the index of the
*next* free slot. The table-level `freeHead` field is the head of the chain;
`NoFreeSlot` (`0xFFFFFFFFu`) is the sentinel "end of list" marker.

```
freeHead → slot[5].Ptr=2 → slot[2].Ptr=7 → slot[7].Ptr=NoFreeSlot
```

Allocate pops the head; free pushes a new head — O(1) both ways:

```csharp
// Allocate (pop)
slotIndex = freeHead;
freeHead  = (uint)slots[slotIndex].Ptr.ToInt64();

// Free (push)
slot.Ptr = new IntPtr((long)freeHead);
freeHead = slotIndex;
```

The clever bit: the same 8-byte `Ptr` field that holds an unmanaged pointer
when the slot is live holds a 32-bit free-list index when it's dead. The
generation high bit (§2.2) is the discriminator — you can always tell which
mode the field is in.

`ClearAllSlots` rebuilds the entire chain in one pass, threading every slot
into the free list in index order: `freeHead → 0 → 1 → 2 → … → highWater−1`.

### 3.2 Slab chains — per-size-class slab lists

Each size class has an "active slab" pointer (`activeSlabs[sizeClass]`),
plus a flat `allSlabs` list used for address-based lookup during free.

The active slab is the one with at least one free cell. When it fills:

```
activeSlabs[2] → SlabA(full) → search allSlabs → SlabB(non-full)
                                                 OR new slab if none
```

Code:

```csharp
var slab = activeSlabs[sizeClass];
if (slab is null || slab.IsFull) {
    slab = FindNonFullSlabInClass(sizeClass) ?? AllocateNewSlab(sizeClass);
    activeSlabs[sizeClass] = slab;
}
```

The `SegmentedSlab.NextInClass` property exists for a future optimisation
(threading the chain explicitly), but the current implementation walks
`allSlabs` linearly. This is fine because:
- Slab count grows slowly (each holds 256 cells by default).
- `Contains(ptr)` is a cheap pointer-range check.
- The slab list is read-mostly after warm-up.

### 3.3 Arena bins — doubly-linked, headers in unmanaged memory

This is the most interesting list. Each free block in an arena segment carries
its own 16-byte header **inside** the freed bytes:

```csharp
[StructLayout(LayoutKind.Sequential, Size = 16)]
internal struct SegmentedFreeBlockHeader
{
    public int SizeBytes;
    public int NextOffset;   // -1 = end
    public int PrevOffset;   // -1 = head
    public int BinIndex;
}
```

Crucially this struct is never instantiated on the managed heap — it's read
and written via `unsafe` pointer cast directly to the segment's unmanaged
buffer:

```csharp
private unsafe SegmentedFreeBlockHeader ReadHeader(int offset) =>
    *(SegmentedFreeBlockHeader*)(Buffer.ToInt64() + offset);
```

So the same 16 bytes are a free-list node when the block is free and the first
16 bytes of string data when it's allocated. Free-list bookkeeping costs zero
managed memory.

The 16 bins live in `int[] binHeads`, each holding the offset of the head
block (or `-1` if the bin is empty):

```
binHeads[3] → @1024(48B) ↔ @4096(56B) ↔ @8192(48B) → -1
                  prev=-1                     next=-1
```

Allocation searches from the smallest sufficient bin upward, walking the
chain until it finds a block large enough. If the block is bigger than needed,
the remainder is split off, given a fresh header, and linked into its own bin
(`LinkIntoBin` writes prev/next/bin and updates `binHeads`).

Free-block coalescing happens on every `Free` call, before the new block is
linked in:

```csharp
TryCoalesceForward (ref offset, ref size);   // merge with successor if free
TryCoalesceBackward(ref offset, ref size);   // merge with predecessor if free
```

Both helpers scan all 16 bins for a neighbour at the right offset, unlink it,
and absorb its size. This is O(total free blocks) per free — fine for typical
workloads, and correct without needing a sorted address index. If you ever
profile hotspots, this is the obvious place to add a per-segment offset map.

---

## 4. End-to-end: a single Allocate + Free

To tie it together, here's what happens when you call
`pool.Allocate("hello world")` (11 chars, slab tier):

1. **Tier choice** — 11 ≤ 128 → slab tier.
2. **Size class** — `ChooseSizeClass(11) = 1` (16-char class, 32-byte cells).
3. **Active slab** — `activeSlabs[1]` either reused or newly allocated.
4. **Bitmap pop** — `TrailingZeroCount` finds first free cell, bit cleared,
   pointer computed as `slab.Buffer + cellIndex * 32`.
5. **Tag the pointer** — `(ptr & ~7L) | 0` (slab tier = 0, no-op here).
6. **Slot allocate** — pop `freeHead` (or bump `highWater`), bump generation,
   store tagged pointer + length.
7. Return `PooledStringRef(pool, slotIndex, newGen)`.

Then `Dispose()` (or explicit `FreeSlot`) does:

1. **Slot lookup** — `TryReadSlot(slotIndex, generation)` checks the slot is
   live and the generation matches.
2. **Untag** — extract raw pointer and tier from the stored field.
3. **Tier dispatch** — slab tier → `LocateSlabByPointer` → `slab.FreeCell` →
   bitmap bit set back to free.
4. **Slot free** — bump generation, set high bit, push slot index onto
   `freeHead` chain.

Any subsequent operation through the same `PooledStringRef` will fail the
generation check at step 1 and throw "stale or freed" — no use-after-free,
no risk of resurrected references.

---

## 5. Summary of the bit/list inventory

| Mechanism                     | Storage cost                  | Lookup cost                |
|-------------------------------|-------------------------------|----------------------------|
| Slot free chain               | 0 (reuses `Ptr` field)        | O(1) push / O(1) pop       |
| Slot generation flag          | 1 bit of existing 32-bit gen  | O(1) check on every read   |
| Pointer tier tag              | 1 bit of existing 64-bit ptr  | O(1) mask                  |
| Slab cell bitmap              | 1 bit per cell, managed array | O(1) `tzcnt` per allocate  |
| Slab chain per size class     | 5 active-slab pointers + list | O(slabs) on chain miss     |
| Arena free-block header       | 16 bytes inside freed memory  | O(1) link/unlink           |
| Arena bin heads               | `int[16]` per segment         | O(blocks in bin) per alloc |

Every "extra" data structure either reuses bits/bytes that were already there
or is a fixed-size managed array. There is no per-allocation managed
allocation in steady state.
