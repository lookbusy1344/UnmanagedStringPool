# SegmentedStringPool — Internals

A walk through the data structures behind `SegmentedStringPool`, focused on
the two recurring tricks: **bitmask reinterpretation** (multiple fields
packed into one word; one field meaning two different things depending on
state) and **intrusive linked lists** (links threaded through the data
they describe, no separate node objects).

This document assumes you've read the design spec
(`docs/superpowers/specs/2026-04-17-segmented-string-pool-design.md`) and
want the implementation-level "how does it actually work" picture.

---

## Orientation

**Vocabulary.** Five container words recur throughout; they are not
interchangeable:

| Word    | What it is                                                      |
|---------|-----------------------------------------------------------------|
| **Slot**    | One row of `SegmentedSlotTable`: `(Ptr, LengthChars, Generation)`. The public handle (`PooledStringRef`) is really a slot index plus a generation number. One slot per live string. |
| **Slab**    | One unmanaged buffer in the slab tier, carved into fixed-size cells. Holds many strings belonging to one size class. |
| **Cell**    | One fixed-size slice of a slab. Holds exactly one string.       |
| **Segment** | One ~1 MB unmanaged buffer in the arena tier. Holds many variable-size blocks. |
| **Block**   | One variable-size slice of a segment. Holds exactly one string when live, or a 16-byte link header followed by dormant bytes when free. |

**Unit of length.** .NET strings are UTF-16: each `char` is 2 bytes. A
40-char string occupies 80 bytes of unmanaged memory. All char↔byte
conversions below follow from that.

**Why "segmented".** The original `UnmanagedStringPool` allocates one
contiguous unmanaged block and grows by copying. `SegmentedStringPool`
maintains many smaller buffers of two kinds (slabs and segments), added
on demand. Nothing ever moves once allocated, so raw pointers stay valid
until explicitly freed.

**Thread safety.** Not thread-safe. None of `SegmentedSlotTable`,
`SegmentedSlabTier`, or `SegmentedArenaTier` use locks or atomics. Callers
are responsible for any required synchronization — even concurrent reads
can race against a concurrent mutation.

---

## 1. The big picture

Every string you allocate ends up in exactly one of two places:

| Small strings (`length ≤ 128` chars) | Large strings (`length > 128` chars) |
|--------------------------------------|--------------------------------------|
| **Slab tier** — fixed-size cells     | **Arena tier** — variable-size blocks |
| Exactly one string per cell          | Exactly one string per block         |
| Cells grouped into *slabs*; slabs grouped into 5 size-class chains | Blocks grouped into *segments*; segments held in an unordered list |

Neither tier ever packs multiple strings into a single cell/block. The
packing happens one level up: a slab holds many cells, a segment holds
many blocks.

Callers never touch raw pointers. They get back a `PooledStringRef`
containing `(pool, slotIndex, generation)` — 16 bytes, fully copyable.
The `SegmentedSlotTable` resolves that handle to a pointer on every read.

```
Allocate(span) → SlabTier or ArenaTier → unmanaged ptr → SlotTable → PooledStringRef
```

The three subsystems:

- **`SegmentedSlotTable`** — managed array of 16-byte `SegmentedSlotEntry`
  records: `{ IntPtr Ptr, int LengthChars, uint Generation }`. This is the
  only managed storage that grows with the live-string count.
- **`SegmentedSlabTier`** — owns **five independent singly-linked lists**
  of slabs, one per size class (cell widths of 8, 16, 32, 64, 128 chars).
  Each slab is one unmanaged buffer carved into fixed-size cells, plus a
  managed bitmap marking which cells are in use.
- **`SegmentedArenaTier`** — owns a `List<SegmentedArenaSegment>`. Each
  segment is ~1 MB of unmanaged memory with a bump pointer at the tail
  and a coalesced free-list at the head. The free-list has 16 bins
  indexed by `Log2(block_size)`.

Two concrete examples of where a string lands:

```
"hello world" (11 chars)
  11 ≤ 128              → slab tier
  ChooseSizeClass(11)=1 → 16-char size class (cells of 32 bytes)
  head of list[1] = SlabA → bitmap tzcnt picks free cell
```

```
a 5000-char document
  5000 > 128            → arena tier
  5000 × 2 = 10000 B    → aligned to 10000 B
  walks segment bins from bin 9 (≥8 KB) upward; else bumps tail
```

**Why two tiers?** Slab allocate and free are O(1) because every cell in a
size class is identical — one bitmap word tells you everything. Arenas
can't do that because block sizes vary, so they pay O(free blocks) for
coalescing in exchange for holding arbitrarily large strings. Routing
small strings through the specialised tier keeps the hot path fast and
reserves the heavier machinery for the few large allocations that need
it.

**Growth.** Every subsystem grows independently, and growth is purely
additive — nothing ever copies or moves:

- **Slot table** doubles its managed array when `highWater` (the count of
  slot indices ever touched) reaches `Capacity`.
- **Slab tier** allocates a fresh slab (a new unmanaged buffer) whenever a
  size-class chain is empty. Individual slabs never resize.
- **Arena tier** appends a new segment (default 1 MB) when every existing
  segment fails to satisfy a request. Individual segments never resize.

That "nothing moves" property is load-bearing: the slot table stores raw
unmanaged pointers, so any relocation would invalidate them.

---

## 2. Reinterpreting bits and bytes

Every field in this pool does two jobs. Here's how each one is carved up.

### 2.1 Tagged pointer — tier bit in the low bit

`Marshal.AllocHGlobal` guarantees 8-byte alignment on every supported
platform. That makes the low **3 bits** of every raw pointer zero — free
real estate. Bit 0 is used to record which tier owns the block.

```
raw pointer  : xxxx…xxxx xxxx…xxxx xxxxx000     (low 3 bits guaranteed 0)
tagged ptr   : xxxx…xxxx xxxx…xxxx xxxxxxxT     (T = tier: 0 slab, 1 arena)
```

Encoding (`SegmentedStringPool.Allocate`):

```csharp
var taggedPtr = new IntPtr((ptr.ToInt64() & PtrMask) | (long)tier);
// PtrMask = ~7L
```

Decoding (`SegmentedStringPool.FreeSlot`):

```csharp
var raw  = new IntPtr(entry.Ptr.ToInt64() & PtrMask);      // clear low bits
var tier = (int) (entry.Ptr.ToInt64() & TierTagMask);      // isolate bit 0
```

Worked example. Arena tier returned raw pointer `0x7FFF_AABBCC00`:

```
raw            : 0x7FFF_AABBCC00   (aligned, low 3 bits = 000)
raw & ~7       : 0x7FFF_AABBCC00   (unchanged)
OR (tier = 1)  : 0x7FFF_AABBCC01   ← this is what slot.Ptr stores
read back & ~7 : 0x7FFF_AABBCC00   (pointer recovered)
read back &  1 : 0x0000_00000001   → tier = 1 → arena
```

The tag travels with the pointer in the same 64-bit field, costing zero
extra bytes per slot.

### 2.2 Generation — `[free flag | 31-bit counter]`

Each slot carries a `uint Generation`. Bit 31 is the free flag; the low
31 bits are a monotonically-increasing reuse counter:

```
generation : F cccccccc cccccccc cccccccc ccccccc
             ^ └───────────── 31-bit counter ──┘
             │
             └─ bit 31: 1 = freed, 0 = live
```

The two helpers:

```csharp
public const uint HighBit        = 0x80000000u;
public const uint GenerationMask = 0x7FFFFFFFu;

public static uint MarkFreeAndBumpGen(uint gen) =>
    ((GenerationValue(gen) + 1u) & GenerationMask) | HighBit;

public static uint ClearFreeAndBumpGen(uint gen) =>
    (GenerationValue(gen) + 1u) & GenerationMask;
```

Every state change bumps the counter. `PooledStringRef` captures the
generation at allocation; any read compares against the live slot's
generation, so a stale ref reusing the same slotIndex is always detected.
Collision only happens after 2³¹ reuses *of the same slot* — impossible
in practice.

Worked sequence on one slot:

```
initial       : 0x0000_0000   (flag=0, counter=0; fresh)
after alloc   : 0x0000_0001   (flag=0, counter=1; live)
after free    : 0x8000_0002   (flag=1, counter=2; free)
after realloc : 0x0000_0003   (flag=0, counter=3; live)
a ref holding 0x0000_0001 now reads → mismatch → throw "stale or freed"
```

### 2.3 Slab bitmap — `1 = free, 0 = used`

A slab with `CellCount` cells carries a managed `ulong[] bitmap` of
`⌈CellCount/64⌉` words. The convention `1 = free` is deliberate:
`BitOperations.TrailingZeroCount(word)` finds the first **set** bit, which
under this convention is the first **free** cell directly — no complement
needed. On x86 it lowers to a single `tzcnt`.

```
bitmap[0] : 1111_1110 1111_0110    (low bit = cell 0, on the right)
                          ^^^ used cells: 0 and 3
            tzcnt → bit 1 (lowest 1) → allocate cell 1
            mark used: word &= ~(1UL << 1)
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

Slabs are initialised with every bit `1` (`ulong.MaxValue`); any "phantom"
bits past `CellCount` in the last word are cleared so they can never be
picked.

### 2.4 Arena bin index — `Log2(size) − 4`

Each arena segment keeps 16 free-list heads in an `int[16]`. Bin index:

```csharp
var bin = BitOperations.Log2((uint)size) - 4;
```

The `− 4` normalises to bin 0 because the minimum block size is 16 bytes
and `Log2(16) = 4`:

```
size   log2  bin    covers
 16 →   4  →  0     [16,    32)
 32 →   5  →  1     [32,    64)
 64 →   6  →  2     [64,   128)
128 →   7  →  3     [128,  256)
…
524288 → 19 → 15    [524288, ∞)  (clamped)
```

Allocation starts at the smallest sufficient bin and walks upward: a
40-byte request rounds up to 64, starts at bin 2, and only moves to bin
3 or higher if bin 2 has no block large enough.

---

## 3. Linked lists

Three different linked-list *shapes* appear. All three are **intrusive** —
the links live inside the data they describe. No `LinkedListNode<T>`
wrapping, no per-node managed allocation.

### 3.1 Slot free chain — singly-linked through the `Ptr` field

The `SegmentedSlotEntry.Ptr` field has two meanings depending on whether
the slot is live or free:

```
live slot : Ptr = tagged unmanaged pointer   (bit 0 = tier, bits 3..63 = addr)
free slot : Ptr = next free slot index       (bits 0..31), bits 32..63 = 0
```

The generation high bit (§2.2) tells you which interpretation applies
without ambiguity. **No separate free-list array exists** — the dormant
`Ptr` field carries the link.

```
freeHead = 5
           ↓
     slots[5].Ptr = 2  →  slots[2].Ptr = 7  →  slots[7].Ptr = 0xFFFFFFFF   (end sentinel)
     (all three have generation bit 31 = 1)
```

Allocate pops the head; free pushes a new head — O(1) both ways.

```csharp
// Allocate (pop)
slotIndex = freeHead;
freeHead  = (uint)slots[slotIndex].Ptr.ToInt64();

// Free (push)
slot.Ptr = new IntPtr((long)freeHead);
freeHead = slotIndex;
```

The allocator prefers popping `freeHead`; if the chain is empty, it bumps
`highWater` (the watermark of slots ever touched) and uses the next
never-before-allocated index, growing the underlying array by doubling
when `highWater` hits `Capacity`. Slots past `highWater` are always in
their zero-initialised state.

`ClearAllSlots` rebuilds the chain in one pass, threading every slot in
index order: `freeHead = 0 → 1 → 2 → … → highWater−1 → NoFreeSlot`.

### 3.2 Slab chains — **five** independent lists, one per size class

This is the concept most likely to confuse readers of the spec. The slab
tier field `activeSlabs` is a small fixed array; each entry is the head
of its own singly-linked list:

```
activeSlabs : SegmentedSlab?[5]
  [0]  8-char cells ( 16 B) → SlabP → SlabQ → null
  [1] 16-char cells ( 32 B) → SlabR → null
  [2] 32-char cells ( 64 B) → null                 (no slab yet in this class)
  [3] 64-char cells (128 B) → SlabS → null
  [4] 128-char cells (256 B) → SlabT → SlabU → SlabV → null
```

So yes — **five entirely separate linked lists**. Each list contains only
slabs of that one size class; a 32-char slab never appears in the 128-char
list. A request for a 40-char string is routed exclusively through list
[3] (64-char cells, since 40 ≤ 64). Links are threaded through
`SegmentedSlab.NextInClass`.

**Why the 8/16/32/64/128 progression?** Geometric doubling caps internal
fragmentation. A string of `N` chars always lands in a cell of at most
`2N` chars, so wasted space per live allocation stays below 50%. A denser
progression — classes every 4 chars, say — would mean more chains, more
slabs, colder caches, for marginal fragmentation gain.

**Fragmentation cost.** A 2-char string still occupies a full 8-char cell
(16 bytes of unmanaged memory). That's real overhead, deliberately traded
for O(1) allocate, O(1) free, and zero per-allocation metadata — every
slab-tier allocation skips the per-block header and variable-size
bookkeeping that the arena tier pays.

**Chain invariant:** every slab on a chain has at least one free cell. A
slab drops off its chain the moment it fills; freeing a cell on a
previously-full slab re-links it at the head of its chain.

That invariant reduces allocation to "read the head":

```csharp
var slab = activeSlabs[sizeClass] ?? AllocateNewSlab(sizeClass);
_ = slab.TryAllocateCell(out var cellIndex);
if (slab.IsFull) {
    DetachHead(sizeClass);              // pop from chain
}
```

And free reduces to "re-link only on the full→non-full transition":

```csharp
public void Free(IntPtr ptr, SegmentedSlab slab) {
    var wasFull = slab.IsFull;
    slab.FreeCell(slab.CellIndexFromOffset(offset));
    if (wasFull) {
        LinkAtHead(SizeClassForCellBytes(slab.CellBytes), slab);
    }
}
```

A separate flat `List<SegmentedSlab> allSlabs` tracks **every** slab
regardless of chain state. It exists only so `LocateSlabByPointer` can
find the owning slab when freeing a raw pointer — full slabs are off
their chain but must still be resolvable.

Worked sequence (size class [0], `cellsPerSlab = 4` for illustration; the
production default is 256):

```
allocate ×4  → SlabA created, fills → detached         chain: (empty)
allocate ×1  → SlabB created (prepended)               chain: SlabB
free firstA  → SlabA was full → re-link at head        chain: SlabA → SlabB
allocate ×1  → from SlabA head (1 free cell)           chain: SlabA → SlabB
                SlabA fills again → detached           chain: SlabB
allocate ×3  → from SlabB until full → detached        chain: (empty)
allocate ×1  → SlabC created                           chain: SlabC
```

Why singly-linked is enough: every mutation hits the head (prepend on
re-link or new, pop on fill). Mid-list nodes are never touched, so a
`Prev` pointer would be dead weight.

### 3.3 Arena bins — doubly-linked, headers *inside* the freed bytes

Arena free blocks carry their own link headers **in the very memory they
describe**:

```csharp
[StructLayout(LayoutKind.Sequential, Size = 16)]
internal struct SegmentedFreeBlockHeader
{
    public int SizeBytes;    // total block size (including this 16-byte header)
    public int NextOffset;   // −1 = end of bin
    public int PrevOffset;   // −1 = head of bin
    public int BinIndex;     // which bin this block is threaded through
}
```

The struct is never instantiated on the managed heap — it's read and
written via `unsafe` pointer cast directly into the segment buffer:

```csharp
private unsafe SegmentedFreeBlockHeader ReadHeader(int offset) =>
    *(SegmentedFreeBlockHeader*)(Buffer.ToInt64() + offset);
```

So the same 16 bytes of a block mean wildly different things depending on
whether that block is live or free:

```
live block (holds an allocated string):
  +0  [char0][char1][char2][char3] …  — UTF-16 payload
  (no header; length comes from the slot entry's LengthChars)

free block (on a bin chain):
  +0   SizeBytes   (int)         ┐
  +4   NextOffset  (int, -1=tail) │  16-byte header
  +8   PrevOffset  (int, -1=head) │
  +12  BinIndex    (int)         ┘
  +16  (unused but reserved as part of the block)
```

Allocating a block overwrites the header with string data. Freeing
restores it. Free-list bookkeeping therefore costs **zero managed
memory** per block.

Bin heads live in `int[] binHeads` on the segment:

```
binHeads[3] → @1024 (48 B) ↔ @4096 (56 B) ↔ @8192 (48 B) → -1
               prev=-1                          next=-1
```

Allocation walks from the smallest sufficient bin upward. If the chosen
block is oversized, the allocator splits off the `[taken | remainder]`
tail, writes a fresh header into it, and re-links it to the head of its
own bin (which may or may not be the original bin).

Free-block coalescing happens on every `Free` call, before the new block
is linked in:

```csharp
TryCoalesceForward(ref offset, ref size);    // merge with successor if free
TryCoalesceBackward(ref offset, ref size);   // merge with predecessor if free
```

Each helper scans all 16 bins looking for a neighbour at the right
offset, unlinks it, and absorbs its size. This is O(total free blocks)
per free — fine for typical workloads, and avoids the complexity of a
sorted address index. If it ever showed up as a hotspot, an
offset-indexed map per segment would be the obvious fix.

Why *this* list is doubly-linked (unlike the slab chain): arbitrary
mid-list blocks get unlinked during allocation (remove the selected
block), during split (remove and re-bin), and during coalescing (remove
the neighbour). A `PrevOffset` makes each unlink O(1) without walking
the chain.

---

## 4. End-to-end: a single Allocate + Free

Putting it all together, `pool.Allocate("hello world")` (11 chars → slab
tier) does:

1. **Tier choice** — 11 ≤ 128 → slab tier.
2. **Size class** — `ChooseSizeClass(11) = 1` (16-char cells, 32 bytes each).
3. **Chain head** — read `activeSlabs[1]`; if null, allocate a new slab
   and prepend. Otherwise use the existing head — the invariant guarantees
   ≥1 free cell.
4. **Bitmap pop** — `tzcnt` picks the first free bit, flip it to 0,
   compute cell pointer as `slab.Buffer + cellIndex * 32`.
5. **Detach if now full** — if `IsFull`, unlink from chain head.
6. **Tag the pointer** — `(ptr & ~7L) | 0` (slab tier → bit 0 stays 0).
7. **Slot allocate** — pop `freeHead` (or bump `highWater`), clear the
   free flag and bump the generation counter, store tagged pointer +
   length.
8. Return `PooledStringRef(pool, slotIndex, newGen)`.

Then `Dispose()` (or explicit `FreeSlot`) does:

1. **Slot lookup** — `TryReadSlot(slotIndex, generation)` checks the slot
   is live and the generation matches.
2. **Untag** — extract raw pointer and tier from the stored field.
3. **Tier dispatch** — slab tier: `LocateSlabByPointer` → `slabTier.Free`
   → set the bitmap bit back to 1; if the slab was previously full,
   re-link at the chain head.
4. **Slot free** — bump generation, set the high bit, push slot index
   onto the `freeHead` chain.

Any subsequent use of the same `PooledStringRef` now fails the generation
check at step 1 and throws "stale or freed" — no use-after-free, no
resurrection.

---

## 5. Inventory

| Mechanism                     | Storage cost                       | Lookup cost                    |
|-------------------------------|------------------------------------|--------------------------------|
| Slot free chain               | 0 (reuses `Ptr` field)             | O(1) push / O(1) pop           |
| Slot generation flag          | 1 bit of existing 32-bit gen       | O(1) check on every read       |
| Tier tag in pointer           | 1 bit of existing 64-bit ptr       | O(1) mask                      |
| Slab cell bitmap              | 1 bit per cell, managed array      | O(1) `tzcnt` per allocate      |
| Slab size-class chains (×5)   | 5 head pointers + `NextInClass`    | O(1) head check, O(1) re-link  |
| Arena free-block header       | 16 bytes inside freed memory       | O(1) link/unlink               |
| Arena bin heads               | `int[16]` per segment              | O(blocks in bin) per allocate  |

Every "extra" data structure either reuses bits/bytes already present or
is a fixed-size managed array. Steady-state allocation performs zero
managed allocations.
