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

**Vocabulary.** Six words recur throughout; they are not interchangeable.
The most important distinction: **slots are not slabs, and slots are not
arenas.** A slot is a managed lookup record. Slabs and arena segments are
unmanaged memory buffers. Every live string has exactly one slot *and*
exactly one home in either a slab cell or an arena block — never both.

| Word    | Layer | What it is                                                      |
|---------|-------|-----------------------------------------------------------------|
| **Slot**    | Index | One row of `SegmentedSlotTable`: `(Ptr, LengthChars, Generation)`. A managed indirection record that maps a handle to a raw pointer. The public handle (`PooledStringRef`) is really a slot index plus a generation number. One slot per live string. **A slot is not storage** — it points to storage in one of the two tiers below. |
| **Slab**    | Storage | One unmanaged buffer in the slab tier, carved into fixed-size cells. Holds many strings belonging to one size class. |
| **Cell**    | Storage | One fixed-size slice of a slab. Holds exactly one string's bytes. |
| **Segment** | Storage | One ~1 MB unmanaged buffer in the arena tier. Holds many variable-size blocks. |
| **Block**   | Storage | One variable-size slice of a segment. Holds exactly one string when live, or a 16-byte link header followed by dormant bytes when free. |
| **Ref**     | Handle | A `PooledStringRef` — the 16-byte value type callers hold. Contains `(pool, slotIndex, generation)`. This is the only thing external code ever touches. |

```
Layer diagram:

  Handle layer:    PooledStringRef  (what the caller holds — pool + slotIndex + generation)
                        │
  Index layer:     SegmentedSlotTable  (managed array — maps slotIndex → tagged pointer + length)
                        │
                   ┌────┴────┐
  Storage layer:   Slab      Arena Segment
                   (cells)   (blocks)
                        │
  Unmanaged memory:  raw bytes of the string (UTF-16 chars)
```

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

### How a PooledStringRef resolves to a string

A `PooledStringRef` contains three fields: a reference to the pool, a
`slotIndex` (which row of the slot table), and a `generation` (which
version of that row). It knows nothing about slabs or arenas. The full
resolution path when you call `ref.AsSpan()`:

```
1. ref.AsSpan()
   │
   └─→ pool.ReadSlot(slotIndex: 42, generation: 3)
        │
        │  "Is this ref still valid?"
        ├─→ slots.TryReadSlot(42, 3)
        │     slots[42].Generation == 3?  → yes, slot is live and matches
        │     returns SegmentedSlotEntry {
        │       Ptr         = 0x7FFF_00A0_0041   (tagged pointer)
        │       LengthChars = 11
        │       Generation  = 3
        │     }
        │
        │  "Where are the bytes?"
        ├─→ raw pointer = Ptr & ~7  →  0x7FFF_00A0_0040  (clear tag bits)
        │   tier tag    = Ptr & 1   →  1                  (arena tier)
        │
        │   (The tag tells us this string lives in an arena segment,
        │    but we don't need to find the segment just to read —
        │    the raw pointer already points directly at the chars.)
        │
        │  "Build the span"
        └─→ new ReadOnlySpan<char>(rawPtr, 11)
             │
             └─→ 11 UTF-16 chars starting at 0x7FFF_00A0_0040
                 This memory is inside some arena segment's buffer.
```

The critical thing: **reading a string never touches the slab tier or
arena tier objects.** The slot entry's tagged pointer goes directly to the
unmanaged bytes. The tier tag (bit 0) is only needed during `Free`, when
the pool must route the deallocation to the correct tier. During reads,
the tag is masked off and ignored.

If the ref were for a slab-tier string, the only difference would be
`Ptr & 1 == 0` — the raw pointer would point into a slab's buffer
instead of a segment's buffer. The read path is identical either way.

**When resolution fails:**

```
ref.AsSpan()  with slotIndex: 42, generation: 1
  │
  └─→ slots.TryReadSlot(42, 1)
         slots[42].Generation == 0x8000_0002   (free flag set, counter=2)
         1 ≠ 0x8000_0002  →  mismatch
         → throws "PooledStringRef is stale or freed"
```

This happens when the string has been freed (or the slot reused for a
different string). The generation mismatch catches it regardless of
whether the original string lived in a slab or arena — the slot table is
the single authority for "is this ref still valid?"

---

## 1. How the pool, tiers, slabs, and segments connect

### 1.1 The ownership graph

`SegmentedStringPool` is the public entry point. It owns three private
fields that together manage all state:

```
SegmentedStringPool  (the object callers create and interact with)
 │
 ├── SegmentedSlotTable  slots        ← managed array of slot entries (handle → pointer)
 │
 ├── SegmentedSlabTier   slabTier     ← owns all slabs (small strings, ≤128 chars)
 │    │
 │    ├── activeSlabs[5]              ← 5 chain heads, one per size class
 │    │    [0] → Slab → Slab → null      (8-char cells)
 │    │    [1] → Slab → null             (16-char cells)
 │    │    [2] → null                    (32-char cells, none allocated yet)
 │    │    [3] → Slab → null             (64-char cells)
 │    │    [4] → Slab → Slab → null      (128-char cells)
 │    │
 │    └── allSlabs: List<Slab>        ← flat list of every slab (full or not)
 │
 └── SegmentedArenaTier  arenaTier    ← owns all segments (large strings, >128 chars)
      │
      └── segments: List<Segment>     ← flat list of all arena segments
           [0] → SegmentedArenaSegment  (1 MB buffer + 16 bin heads + bump pointer)
           [1] → SegmentedArenaSegment
           …
```

The pool constructor (`SegmentedStringPool(options)`) creates all three
subsystems. They are constructed empty — no unmanaged memory is allocated
until the first `Allocate` call. The pool passes configuration down:

- `slots` receives `InitialSlotCapacity` (default 64 entries)
- `slabTier` receives `SlabCellsPerSlab` (default 256 cells per slab)
- `arenaTier` receives `ArenaSegmentBytes` (default 1 MB per segment)

### 1.2 How an allocation flows through the graph

When you call `pool.Allocate("hello")`, the pool makes one routing
decision based on the string's char count vs. `smallThreshold` (default
128). That decision determines which tier receives the allocation:

```
pool.Allocate(span)
  │
  ├─ length ≤ 128? ──→ slabTier.Allocate(charCount)  → returns raw IntPtr
  │                         │
  │                         └─ picks size class → picks/creates slab → bitmap picks cell
  │
  └─ length > 128?  ──→ arenaTier.Allocate(byteCount) → returns raw IntPtr
                            │
                            └─ tries each segment's free bins → bump fallback → new segment
  │
  ▼
  tag the pointer (bit 0 = which tier)
  │
  ▼
  slots.Allocate(taggedPtr, length) → returns (slotIndex, generation)
  │
  ▼
  return PooledStringRef(pool, slotIndex, generation)  ← 16 bytes, given to caller
```

The caller gets back a `PooledStringRef` — a 16-byte value type containing
`(pool reference, slotIndex, generation)`. This is the **only** thing the
caller ever holds. They never see raw pointers, tiers, slabs, or segments.

When the caller later reads the string (via `ref.AsSpan()`), the path is:

```
ref.AsSpan()
  → pool.ReadSlot(slotIndex, generation)
    → slots.TryReadSlot(slotIndex, generation) → SegmentedSlotEntry { Ptr, LengthChars }
    → untag Ptr (mask off bit 0) → raw pointer
    → new ReadOnlySpan<char>(rawPtr, lengthChars)
```

### 1.3 How disposal flows through the graph

When `pool.Dispose()` is called, it tears down everything:

```
pool.Dispose()
  → slots.ClearAllSlots()         ← marks every slot as freed, rebuilds free chain
  → slabTier.Dispose()            ← iterates allSlabs, calls Marshal.FreeHGlobal on each buffer
  → arenaTier.Dispose()           ← iterates segments, calls Marshal.FreeHGlobal on each buffer
```

After disposal, the pool sets `disposed = true`. Any subsequent call to
`Allocate`, `ReadSlot`, or `Clear` throws `ObjectDisposedException`. Calls
to `FreeSlot` silently return (defensive — a `PooledStringRef.Dispose()`
racing against pool disposal shouldn't crash).

---

## 2. What is a slab?

A **slab** (`SegmentedSlab`) is a single contiguous block of unmanaged
memory, divided into fixed-size cells. Every cell in a slab is the same
size. A slab holds strings of one size class only.

### 2.1 Physical layout

A slab with `CellBytes = 32` (16-char cells) and `CellCount = 256`:

```
Buffer (unmanaged, allocated via Marshal.AllocHGlobal):
┌──────────┬──────────┬──────────┬──────────┬─────┬──────────┐
│ Cell 0   │ Cell 1   │ Cell 2   │ Cell 3   │ ... │ Cell 255 │
│ 32 bytes │ 32 bytes │ 32 bytes │ 32 bytes │     │ 32 bytes │
└──────────┴──────────┴──────────┴──────────┴─────┴──────────┘
Total: 32 × 256 = 8,192 bytes of unmanaged memory

Bitmap (managed ulong[] on the GC heap):
┌────────────────────────────────────────────────────────────┐
│ ulong[0]: bits 0–63    (cells 0–63)                        │
│ ulong[1]: bits 0–63    (cells 64–127)                      │
│ ulong[2]: bits 0–63    (cells 128–191)                     │
│ ulong[3]: bits 0–63    (cells 192–255)                     │
└────────────────────────────────────────────────────────────┘
Convention: 1 = free, 0 = used
```

Each cell is a fixed-size slot. A string shorter than the cell size simply
doesn't use the trailing bytes — the actual length is stored in the slot
table, not in the slab. A 3-char string in a 16-char cell occupies 6 of
the 32 bytes; the remaining 26 bytes are wasted but the trade-off is O(1)
alloc/free with zero per-cell metadata.

### 2.2 The five size classes

The slab tier maintains five independent singly-linked lists, one per size
class. Each list is a chain of slabs whose cells are all the same width:

| Index | Cell chars | Cell bytes | Covers strings of length |
|-------|-----------|------------|--------------------------|
| 0     | 8         | 16         | 1–8 chars                |
| 1     | 16        | 32         | 9–16 chars               |
| 2     | 32        | 64         | 17–32 chars              |
| 3     | 64        | 128        | 33–64 chars              |
| 4     | 128       | 256        | 65–128 chars             |

A string is routed to the smallest class that fits. An 11-char string goes
to class 1 (16-char cells), never to class 2 or 3. A 128-char string goes
to class 4 exactly. A 129-char string bypasses the slab tier entirely and
goes to the arena tier.

### 2.3 How slabs link together

Each `SegmentedSlab` has a `NextInClass` field — a nullable reference to
the next slab in the same size-class chain. The `SegmentedSlabTier` holds
the chain heads in `activeSlabs[5]`:

```
activeSlabs[1] ──→ SlabR (NextInClass) ──→ SlabQ (NextInClass) ──→ null
                   16-char cells              16-char cells
                   has free cells             has free cells
```

**Chain invariant:** every slab on a chain has at least one free cell.

When a slab fills up (all cells used), it is **detached** from the chain
head. It still exists in `allSlabs` (the flat list) but is no longer
reachable through the chain — allocation never visits full slabs.

When a cell is freed on a previously-full slab, that slab is **re-linked**
at the head of its size-class chain, making it available for future
allocations again.

A separate flat `List<SegmentedSlab> allSlabs` tracks **every** slab
regardless of chain state. It exists so `LocateSlabByPointer` can find the
owning slab when freeing a raw pointer — full slabs are off their chain but
must still be locatable.

### 2.4 Slab lifetime

Slabs are **never freed** during normal operation. Once allocated, a slab
persists until either:

- `pool.Dispose()` — frees all slab buffers via `Marshal.FreeHGlobal`
- `pool.Clear()` — resets all bitmaps to "all free" and re-threads every
  slab into its chain, but does **not** free the unmanaged memory. The
  slabs are reused for future allocations.

---

## 3. What is an arena segment?

An **arena segment** (`SegmentedArenaSegment`) is a single contiguous block
of unmanaged memory (default 1 MB), used for strings longer than 128 chars.
Unlike a slab's fixed-size cells, segments hold variable-size blocks — each
string gets exactly as many bytes as it needs (after 8-byte alignment).

### 3.1 Physical layout

A segment with `Capacity = 1,048,576` bytes (1 MB):

```
Buffer (unmanaged, 1 MB):
┌─────────────────────────────────────────────────────────────────┐
│ Block A   │ Block B  │ (free)   │ Block D   │    (unused)       │
│ 400 B     │ 1024 B   │ 600 B   │ 2048 B    │                   │
│ (live)    │ (live)   │ (freed)  │ (live)    │← BumpOffset       │
└─────────────────────────────────────────────────────────────────┘
                                               ↑
                                          next bump allocation starts here
```

The segment has two allocation strategies:

1. **Bump allocation** — fast path. Advances `BumpOffset` from the front of
   the buffer. Each new allocation gets the next `size` bytes starting at
   `BumpOffset`. No searching, no overhead. Used when no free-list block is
   available.

2. **Free-list allocation** — reuse path. When strings are freed, their
   memory is recorded as free blocks in 16 segregated bins (indexed by
   `Log2(blockSize)`). Future allocations search the bins for a block that
   fits before falling back to bump.

### 3.2 Free blocks carry their own headers

When a block is freed, the first 16 bytes of that block's memory are
overwritten with a `SegmentedFreeBlockHeader`:

```
Live block:                          Free block:
┌────────────────────────┐           ┌────────────────────────┐
│ h e l l o   w o r l d  │           │ SizeBytes  (int)       │ ← 16-byte header
│  ... UTF-16 chars ...  │           │ NextOffset (int, -1=end│    written directly
│                        │           │ PrevOffset (int, -1=head    into the freed
│                        │           │ BinIndex   (int)       │    memory
│                        │           │ (remaining bytes idle) │
└────────────────────────┘           └────────────────────────┘
```

This is why the minimum block size is 16 bytes — a free block must be large
enough to hold its own link header. The header is never instantiated on the
managed heap; it's read/written via `unsafe` pointer cast directly into the
segment buffer.

### 3.3 The 16 bins

Each segment maintains `int[16] binHeads`, where each entry is an offset
into the buffer (or -1 for empty). The bin index is derived from block size:

```
bin = Log2(size) − 4
```

The `− 4` normalises because the minimum block is 16 bytes and `Log2(16) = 4`:

```
size   log2  bin    covers
 16 →   4  →  0     [16,    32)
 32 →   5  →  1     [32,    64)
 64 →   6  →  2     [64,   128)
128 →   7  →  3     [128,  256)
…
524288 → 19 → 15    [524288, ∞)  (clamped)
```

### 3.4 How segments link together

The `SegmentedArenaTier` holds segments in a flat `List<SegmentedArenaSegment>`.
When allocating, it iterates through all segments in insertion order, trying
each one's bins and bump allocator. If none can satisfy the request, a new
segment is appended to the list:

```
segments: List<SegmentedArenaSegment>
  [0] → Segment (1 MB, BumpOffset=900000, some free blocks in bins)
  [1] → Segment (1 MB, BumpOffset=524288, mostly empty)
  [2] → Segment (2 MB, BumpOffset=0,      freshly allocated for an oversized string)
```

New segments are sized as `max(defaultSegmentBytes, requestedByteCount)`.
A single 2 MB string will create a 2 MB segment dedicated to it.

### 3.5 Segment lifetime

Like slabs, segments are **never freed** during normal operation:

- `pool.Dispose()` — frees all segment buffers via `Marshal.FreeHGlobal`
- `pool.Clear()` — resets every segment's `BumpOffset` to 0 and clears all
  bin heads, but keeps the unmanaged memory. The segments are reused.

---

## 4. Reinterpreting bits and bytes

Every field in this pool does two jobs. Here's how each one is carved up.

### 4.1 Tagged pointer — tier bit in the low bit

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

### 4.2 Generation — `[free flag | 31-bit counter]`

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

### 4.3 Slab bitmap — `1 = free, 0 = used`

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

### 4.4 Arena bin index — `Log2(size) − 4`

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

## 5. Linked lists

Three different linked-list *shapes* appear. All three are **intrusive** —
the links live inside the data they describe. No `LinkedListNode<T>`
wrapping, no per-node managed allocation.

### 5.1 Slot free chain — singly-linked through the `Ptr` field

The `SegmentedSlotEntry.Ptr` field has two meanings depending on whether
the slot is live or free:

```
live slot : Ptr = tagged unmanaged pointer   (bit 0 = tier, bits 3..63 = addr)
free slot : Ptr = next free slot index       (bits 0..31), bits 32..63 = 0
```

The generation high bit (§4.2) tells you which interpretation applies
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

### 5.2 Slab chains — **five** independent lists, one per size class

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

### 5.3 Arena bins — doubly-linked, headers *inside* the freed bytes

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

## 6. End-to-end walkthroughs

### 6.1 Allocating a tiny string: `pool.Allocate("Hi")` (2 chars)

**State before:** Fresh pool, nothing allocated yet. All chains empty, all
segments empty, slot table has 64 pre-allocated entries (all zeroed).

| Step | What happens | Effect on data structures |
|------|-------------|--------------------------|
| 1 | `Allocate` called with 2-char span | Pool checks: `2 ≤ 128` → slab tier |
| 2 | `slabTier.Allocate(2)` called | `ChooseSizeClass(2) = 0` (8-char cells, 16 bytes each) |
| 3 | `activeSlabs[0]` is null | First allocation in this size class! |
| 4 | `AllocateNewSlab(0)` called | Creates a new `SegmentedSlab(cellBytes=16, cellCount=256)`. Allocates `16 × 256 = 4,096 bytes` via `Marshal.AllocHGlobal`. Bitmap: 4 `ulong` words, all bits set to 1. Slab added to `allSlabs` list and linked as head of `activeSlabs[0]`. |
| 5 | `slab.TryAllocateCell()` | `tzcnt(bitmap[0])` = 0 → picks cell 0. Flips bit 0 to 0 in `bitmap[0]`. `freeCells` drops from 256 to 255. |
| 6 | Cell pointer computed | `slab.Buffer + (0 × 16) = slab.Buffer` — the first 16 bytes of the slab. |
| 7 | String data copied | `"Hi"` → 4 bytes (2 chars × 2 bytes/char) written into cell 0's first 4 bytes. The remaining 12 bytes of the 16-byte cell are unused. |
| 8 | Pointer tagged | `(ptr & ~7) | 0` → bit 0 stays 0 (slab tier). |
| 9 | `slots.Allocate(taggedPtr, 2)` | `freeHead` is `NoFreeSlot` (chain empty), so uses `highWater = 0` → slot index 0. `highWater` bumps to 1. Generation set to `0x0000_0001` (counter=1, free flag clear). `activeCount` becomes 1. |
| 10 | Returns `PooledStringRef(pool, 0, 1)` | Caller receives 16-byte handle. |

**State after:**
- 1 slab exists (4,096 bytes unmanaged), 255 of 256 cells free
- `activeSlabs[0]` → this slab (slab is not full, stays on chain)
- Slot 0: `Ptr = tagged pointer to cell 0`, `LengthChars = 2`, `Generation = 1`
- All other `activeSlabs` entries still null — no other size classes touched

### 6.2 Allocating a medium string: `pool.Allocate("Hello World!")` (12 chars)

**State before:** Same as after §6.1 (one slab in class 0, one slot used).

| Step | What happens | Effect |
|------|-------------|--------|
| 1 | `12 ≤ 128` → slab tier | |
| 2 | `ChooseSizeClass(12) = 1` | 16-char cells, 32 bytes each |
| 3 | `activeSlabs[1]` is null | First allocation in class 1 |
| 4 | New slab created | `SegmentedSlab(cellBytes=32, cellCount=256)` → `32 × 256 = 8,192 bytes` of unmanaged memory. Added to `allSlabs` (now 2 slabs total). Linked as `activeSlabs[1]` head. |
| 5 | Cell 0 allocated via bitmap | 24 bytes of string data (`12 × 2`) written into the 32-byte cell. 8 bytes wasted. |
| 6 | Pointer tagged with tier 0 | |
| 7 | Slot 1 allocated | `highWater` bumps to 2. Generation = 1. |
| 8 | Returns `PooledStringRef(pool, 1, 1)` | |

**State after:** Two slabs exist in different size classes. The pool has
allocated 12,288 bytes of unmanaged memory total (4,096 + 8,192) to hold
just 28 bytes of string data. This up-front cost amortises over the next
~510 allocations that will fit into the existing slabs with zero further
unmanaged allocation.

### 6.3 Allocating a large string: `pool.Allocate(doc)` where `doc` is 5,000 chars

**State before:** Two slabs from §6.1–6.2, two slots used.

| Step | What happens | Effect |
|------|-------------|--------|
| 1 | `5000 > 128` → arena tier | |
| 2 | `byteCount = 5000 × 2 = 10,000` | After 8-byte alignment: 10,000 bytes (already aligned). |
| 3 | `arenaTier.Allocate(10000)` | Iterates `segments` list — empty, no segments exist yet. |
| 4 | New segment created | `SegmentedArenaSegment(max(1048576, 10000))` → 1 MB buffer via `Marshal.AllocHGlobal`. `BumpOffset = 0`. All 16 `binHeads` set to -1 (no free blocks). |
| 5 | `segment.TryAllocate(10000)` | Bins are all empty → falls through to bump allocator. `BumpOffset (0) + 10000 ≤ 1048576` → returns `Buffer + 0`. `BumpOffset` advances to 10,000. |
| 6 | 10,000 bytes of string data copied | Written at the start of the 1 MB segment buffer. |
| 7 | Pointer tagged with tier 1 | `(ptr & ~7) | 1` → bit 0 set to 1 (arena tier). |
| 8 | Slot 2 allocated | `highWater` bumps to 3. Generation = 1. |
| 9 | Returns `PooledStringRef(pool, 2, 1)` | |

**State after:** The arena tier now has 1 segment with `BumpOffset = 10000`.
The remaining ~1,038,576 bytes of the segment are available for future large
string allocations without creating a new segment.

### 6.4 Allocating a very large string: `pool.Allocate(huge)` where `huge` is 600,000 chars

This string is 1,200,000 bytes — larger than the default 1 MB segment.

| Step | What happens | Effect |
|------|-------------|--------|
| 1 | `600000 > 128` → arena tier | |
| 2 | `byteCount = 600000 × 2 = 1,200,000` | |
| 3 | Try existing segment (1 MB from §6.3) | `BumpOffset (10000) + 1200000 > 1048576` → doesn't fit. Bins are empty → no free block either. Fails. |
| 4 | New segment created | `max(1048576, 1200000) = 1,200,000` → a 1.2 MB segment, sized exactly for this string. |
| 5 | Bump allocation in new segment | Returns `Buffer + 0`. `BumpOffset` advances to 1,200,000. This segment is now completely full — `BumpOffset == Capacity`. |
| 6 | Tag, slot allocate, return ref | |

**State after:** Two segments exist — the original 1 MB (with 1,038,576
bytes still available) and the new 1.2 MB (completely full). Future large
allocations will try the first segment first (has space), only creating new
segments if needed.

### 6.5 Freeing a small string

Continuing from §6.1: freeing the `PooledStringRef` for `"Hi"`.

| Step | What happens | Effect |
|------|-------------|--------|
| 1 | `ref.Dispose()` → `pool.FreeSlot(0, 1)` | Checks `disposed` → false. |
| 2 | `slots.TryReadSlot(0, 1)` | Slot 0 has generation 1 → match. Returns the entry. |
| 3 | Untag pointer | `entry.Ptr & ~7` → raw pointer to cell 0 of the slab. `entry.Ptr & 1` → tier = 0 (slab). |
| 4 | `slabTier.LocateSlabByPointer(raw)` | Iterates `allSlabs`, checks `slab.Contains(ptr)` by address range → finds the 8-char-class slab. |
| 5 | `slabTier.Free(raw, slab)` | Computes `offset = ptr - slab.Buffer = 0`. `CellIndexFromOffset(0) = 0`. `wasFull = false` (slab had 255 free cells). `slab.FreeCell(0)` → sets bit 0 in `bitmap[0]` back to 1. `freeCells` becomes 256. |
| 6 | Was the slab full before this free? | No (`wasFull = false`) → slab was already on the chain. No re-linking needed. |
| 7 | `slots.Free(0, 1)` | Generation bumped: `0x0000_0001` → `0x8000_0002` (free flag set, counter = 2). `slot.Ptr` overwritten with `freeHead` value (for the free chain). `freeHead` set to 0. `activeCount` drops to 1 (if other allocations exist) or 0. |

**State after:**
- The slab still exists (memory is **not** freed). Cell 0 is marked free in
  the bitmap and available for reuse.
- Slot 0 is on the free chain. Its generation is now `0x8000_0002`. Any
  `PooledStringRef` holding generation `0x0000_0001` will fail the
  generation check and throw "stale or freed".
- The slab's unmanaged buffer (`4,096 bytes`) persists until `pool.Dispose()`.

### 6.6 Freeing a small string from a full slab (re-link scenario)

Imagine size class 0 (8-char cells) with one slab, and all 256 cells have
been allocated. The slab was detached from `activeSlabs[0]` when it filled.

| Step | What happens | Effect |
|------|-------------|--------|
| 1 | Free one of the 256 strings | `pool.FreeSlot(slotIndex, gen)` |
| 2 | Untag, locate slab | Same as §6.5 steps 2–4. |
| 3 | `slabTier.Free(raw, slab)` | `wasFull = true` (slab had 0 free cells). |
| 4 | `slab.FreeCell(cellIndex)` | Sets the bitmap bit back to 1. `freeCells` becomes 1. |
| 5 | **Re-link at head** | Since `wasFull` was true, `LinkAtHead(0, slab)` is called. `slab.NextInClass = activeSlabs[0]` (which may be null or another slab). `activeSlabs[0] = slab`. The slab is back on the chain and available for allocation. |
| 6 | Slot freed | Same as §6.5 step 7. |

**State after:** `activeSlabs[0]` now points to this slab. The next small
string allocation (≤8 chars) will find it immediately and use its one free
cell via `tzcnt`.

### 6.7 Freeing a large string (arena tier)

Continuing from §6.3: freeing the 5,000-char document.

| Step | What happens | Effect |
|------|-------------|--------|
| 1 | `pool.FreeSlot(2, 1)` | Generation check passes. |
| 2 | Untag: `tier = 1` (arena) | |
| 3 | `arenaTier.LocateSegmentByPointer(raw)` | Iterates segments, checks `segment.Contains(ptr)` by address range → finds the first 1 MB segment. |
| 4 | `segment.Free(ptr, 10000)` | `offset = ptr - Buffer = 0`. `size = AlignSize(10000) = 10000`. |
| 5 | `TryCoalesceForward(ref 0, ref 10000)` | Checks if there's a free block at offset `0 + 10000 = 10000`. Since `BumpOffset = 10000` and `10000 >= BumpOffset`, the successor is past the bump — returns immediately. Nothing to coalesce. |
| 6 | `TryCoalesceBackward(ref 0, ref 10000)` | Looks for a free block `X` where `X.offset + X.SizeBytes == 0`. No such block exists (this was the first allocation). Returns immediately. |
| 7 | Write free block header at offset 0 | `SizeBytes = 10000`, `NextOffset = -1`, `PrevOffset = -1`, `BinIndex = Log2(10000) - 4 = 13 - 4 = 9`. The first 16 bytes of the freed 10,000-byte block now hold this header. The string data is overwritten. |
| 8 | `LinkIntoBin(0)` | `binHeads[9]` was -1 (empty) → now `binHeads[9] = 0`. This 10,000-byte free block is now the head (and only entry) of bin 9. |
| 9 | Slot 2 freed | Same generation bump and free-chain push as the slab case. |

**State after:**
- The segment still exists (1 MB of unmanaged memory, not freed).
- Bytes 0–9,999 are now a free block on bin 9. The string's UTF-16 data is
  gone — the first 16 bytes hold the link header, the rest are garbage.
- A future allocation of ~5,000–8,191 chars (10,000–16,383 bytes) would
  find this block via bin 9 and reuse it.
- `BumpOffset` is still 10,000 — bump allocation would continue from there
  for requests that don't fit in the free block.

### 6.8 Freeing adjacent arena blocks (coalescing)

Suppose a segment has three consecutive allocations: A (1,024 B at offset
0), B (2,048 B at offset 1,024), C (512 B at offset 3,072). `BumpOffset`
is 3,584.

**Free B first, then A:**

```
After freeing B:
  No adjacent free blocks → B becomes a free block at offset 1024, size 2048.
  binHeads[7] = 1024  (bin 7 covers [128, 256) but Log2(2048)-4 = 11-4 = 7)

  Segment: [A live 1024B][B FREE 2048B][C live 512B]

After freeing A:
  TryCoalesceForward: is there a free block at offset 0 + 1024 = 1024?
    Scans bins → finds B at offset 1024 in bin 7. Unlinks B.
    size = 1024 + 2048 = 3072.
  TryCoalesceBackward: is there a free block ending at offset 0? No.
  Writes header at offset 0, size 3072 → bin index = Log2(3072)-4 = 11-4 = 7.
  Links into bin 7.

  Segment: [A+B COALESCED FREE 3072B][C live 512B]
```

Without coalescing, two adjacent free blocks of 1,024 + 2,048 bytes could
never satisfy a single 2,500-byte allocation. Coalescing merges them into
one 3,072-byte block that can.

**Then free C:**

```
After freeing C:
  TryCoalesceForward: offset 3072 + 512 = 3584 = BumpOffset → nothing beyond.
  TryCoalesceBackward: is there a free block ending at offset 3072?
    Scans bins → finds the coalesced A+B block at offset 0, size 3072.
    0 + 3072 == 3072 ✓. Unlinks it.
    offset = 0, size = 3072 + 512 = 3584.
  Writes header at offset 0, size 3584 → Links into appropriate bin.

  Segment: [A+B+C ALL COALESCED FREE 3584B]  (BumpOffset still 3584)
```

The entire used portion of the segment is now one contiguous free block.
The next allocation that fits within 3,584 bytes will reuse this space
without advancing the bump pointer.

---

## 7. Empty strings

`Allocate("")` short-circuits before any tier work happens and returns the
singleton `PooledStringRef.Empty`. No slot is consumed, no cell allocated,
no block reserved — both tiers only ever see non-empty inputs.

`PooledStringRef.Empty` is `default(PooledStringRef)`: `Pool = null`,
`SlotIndex = 0`, `Generation = 0`. Its `IsEmpty` check is `Pool is null &&
SlotIndex == 0 && Generation == 0`.

Calling `Dispose()` on `Empty` calls `Pool?.FreeSlot(...)` → `Pool` is
null → no-op. Safe to dispose multiple times or never.

---

## 8. Inventory

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
