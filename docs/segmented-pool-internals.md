# SegmentedStringPool — Internals

## At a glance

The pool stores strings in two tiers of **unmanaged memory** — memory
allocated outside the .NET GC via `Marshal.AllocHGlobal`, which calls
the OS heap allocator (`malloc` on macOS/Linux, `HeapAlloc` on Windows).
The returned `IntPtr` is a raw pointer to bytes the GC will never move,
collect, or scan. This is the entire point: string data lives outside GC
pressure.

```
                  ┌────────────────────────────────────────────────-──────┐
                  │          SegmentedStringPool                          │
                  │                                                       │
                  │  ┌─────────────────────────────────────────────-────┐ │
                  │  │  SLAB TIER  (small strings, ≤128 chars)          │ │
                  │  │                                                  │ │
                  │  │  5 size classes: 8 / 16 / 32 / 64 / 128 chars    │ │
                  │  │       │                                          │ │
                  │  │       ▼                                          │ │
                  │  │  Per class: chain of slabs (linked list)         │ │
                  │  │       │                                          │ │
                  │  │       ▼                                          │ │
                  │  │  Each slab: fixed-size cells (bitmap-tracked)    │ │
                  │  │       │                                          │ │
                  │  │       ▼                                          │ │
                  │  │  Each cell: one small string                     │ │
                  │  └──────────────────────────────────────────────-───┘ │
                  │                                                       │
                  │  ┌─────────────────────────────────────────────-────┐ │
                  │  │  ARENA TIER  (large strings, >128 chars)         │ │
                  │  │                                                  │ │
                  │  │  List of fixed-size segments (~1 MB each)        │ │
                  │  │       │                                          │ │
                  │  │       ▼                                          │ │
                  │  │  Each segment: variable-size blocks              │ │
                  │  │       │        (bump alloc + free-list bins)     │ │
                  │  │       ▼                                          │ │
                  │  │  Each block: one large string                    │ │
                  │  └─────────────────────────────────────────────-────┘ │
                  │                                                       │
                  │  All unmanaged buffers obtained via                   │
                  │  Marshal.AllocHGlobal → OS heap (malloc / HeapAlloc)  │
                  │  Pointers stored as tagged IntPtr in the slot table   │
                  └──────────────────────────────────────────────────-────┘
```

---

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
  Storage layer:  Slab      Arena Segment
                 (cells)    (blocks)
                   │        │
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

When the caller later reads the string (via `ref.AsSpan()`), the slot
table resolves the handle to a raw pointer — see
["How a PooledStringRef resolves to a string"](#how-a-pooledstringref-resolves-to-a-string)
in the Orientation section for the full trace.

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

Each cell is a fixed-size region. A string shorter than the cell size simply
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

### 2.4 Pre-warming with Reserve

`pool.Reserve(chars)` splits the budget between tiers and pre-allocates
capacity. The slab tier's share triggers `AllocateNewSlab` calls for the
largest size class (128-char cells) until enough total capacity exists.
This avoids the first-allocation latency of creating slabs on demand.

### 2.5 Slab lifetime

Slabs are **never freed** during normal operation. Once allocated, a slab
persists until either:

- `pool.Dispose()` — frees all slab buffers via `Marshal.FreeHGlobal`
- `pool.Clear()` — resets all bitmaps to "all free" and re-threads every
  slab into its chain, but does **not** free the unmanaged memory. The
  slabs are reused for future allocations. See §6.14 for a walkthrough.

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
│ 400 B     │ 1024 B   │ 600 B    │ 2048 B    │                   │
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
┌────────────────────────┐           ┌───────────────────-─────┐
│ h e l l o   w o r l d  │           │ SizeBytes  (int)        │ ← 16-byte header
│  ... UTF-16 chars ...  │           │ NextOffset (int, -1=end │    written directly
│                        │           │ PrevOffset (int, -1=head│    into the freed
│                        │           │ BinIndex   (int)        │    memory
│                        │           │ (remaining bytes idle)  │
└────────────────────────┘           └───────────────────-─────┘
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

### 3.5 Pre-warming with Reserve

The arena tier's share of `pool.Reserve(chars)` triggers new segment
allocations until total segment capacity meets the byte budget. Each new
segment uses the default size (1 MB) or whatever is needed to reach the
target.

### 3.6 Segment lifetime

Like slabs, segments are **never freed** during normal operation:

- `pool.Dispose()` — frees all segment buffers via `Marshal.FreeHGlobal`
- `pool.Clear()` — resets every segment's `BumpOffset` to 0 and clears all
  bin heads, but keeps the unmanaged memory. The segments are reused.
  See §6.14 for a walkthrough.

### 3.7 Dead field: `SegmentedArenaSegment.Next`

The segment class declares a `Next` property (`SegmentedArenaSegment?
Next`), but it is never read or written by any production code path. The
arena tier uses a flat `List<SegmentedArenaSegment>` rather than a linked
list of segments, so this field is dead code — likely a leftover from an
earlier design that threaded segments into a chain. It has no effect on
behaviour.

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
             ^ └───────────── 31-bit counter ───┘
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
                           ^  ^ used cells: 0 and 3
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

**State before:** Fresh pool. All chains empty, no segments, slot table
has 64 zeroed entries, `freeHead = NoFreeSlot`, `highWater = 0`.

```
pool.Allocate("Hi")
 │
 │  Route to tier
 ├─ 2 ≤ 128 → slab tier
 │
 │  Pick size class
 ├─ ChooseSizeClass(2) = 0  →  8-char cells (16 bytes each)
 │
 │  Find a slab with space
 ├─ activeSlabs[0] is null  →  no slab exists yet for this class
 │   │
 │   └─ AllocateNewSlab(0)
 │       ├─ new SegmentedSlab(cellBytes=16, cellCount=256)
 │       ├─ Marshal.AllocHGlobal(16 × 256 = 4,096 bytes)
 │       ├─ bitmap: 4 × ulong, all bits = 1 (every cell free)
 │       ├─ allSlabs.Add(slab)                ← tracked in flat list
 │       └─ LinkAtHead(0, slab)               ← slab becomes chain head
 │
 │          Slab chain after:
 │          activeSlabs[0] ──→ SlabA ──→ null
 │
 │  Allocate a cell
 ├─ slab.TryAllocateCell()
 │   ├─ tzcnt(bitmap[0]) = 0              → picks cell 0
 │   ├─ bitmap[0] &= ~(1UL << 0)          → bit 0 flipped to 0 (used)
 │   └─ freeCells: 256 → 255
 │
 │  Compute pointer
 ├─ ptr = slab.Buffer + (0 × 16)          → first 16 bytes of buffer
 │
 │  Copy string data
 ├─ "Hi" → 4 bytes (2 chars × 2 B/char)   → written into cell 0
 │   (remaining 12 bytes of the 16-byte cell are unused)
 │
 │  Tag the pointer
 ├─ taggedPtr = (ptr & ~7) | 0             → bit 0 = 0 (slab tier)
 │
 │  Allocate a slot
 ├─ slots.Allocate(taggedPtr, lengthChars=2)
 │   ├─ freeHead = NoFreeSlot (empty chain) → use highWater
 │   ├─ slotIndex = 0,  highWater: 0 → 1
 │   ├─ generation = ClearFreeAndBumpGen(0x00000000) = 0x00000001
 │   ├─ slot[0] = { Ptr=taggedPtr, LengthChars=2, Generation=0x00000001 }
 │   └─ activeCount: 0 → 1
 │
 │      Slot table after:
 │      [0] Ptr=→SlabA:cell0  Len=2  Gen=0x00000001 (live)
 │      [1…63] (zeroed, never touched)
 │      freeHead = NoFreeSlot,  highWater = 1
 │
 │  Is slab now full?
 ├─ freeCells = 255 → no → slab stays on chain
 │
 └─ return PooledStringRef(pool, slotIndex=0, generation=1)
```

**State after:** 1 slab (4,096 B unmanaged), 255 free cells,
`activeSlabs[0]` → SlabA. 1 slot used. All other size classes untouched.

### 6.2 Allocating a medium string: `pool.Allocate("Hello World!")` (12 chars)

**State before:** One slab in class 0 (from §6.1), one slot used.

```
pool.Allocate("Hello World!")
 │
 │  Route to tier
 ├─ 12 ≤ 128 → slab tier
 │
 │  Pick size class
 ├─ ChooseSizeClass(12) = 1  →  16-char cells (32 bytes each)
 │
 │  Find a slab with space
 ├─ activeSlabs[1] is null  →  first allocation in this size class
 │   │
 │   └─ AllocateNewSlab(1)
 │       ├─ new SegmentedSlab(cellBytes=32, cellCount=256)
 │       ├─ Marshal.AllocHGlobal(32 × 256 = 8,192 bytes)
 │       ├─ allSlabs.Add(slab)                ← now 2 slabs in flat list
 │       └─ LinkAtHead(1, slab)
 │
 │          Slab chains after:
 │          activeSlabs[0] ──→ SlabA ──→ null      (unchanged)
 │          activeSlabs[1] ──→ SlabB ──→ null      (new)
 │          activeSlabs[2…4] = null
 │
 │  Allocate cell, copy data
 ├─ cell 0 picked via tzcnt
 ├─ "Hello World!" → 24 bytes written into the 32-byte cell (8 bytes wasted)
 │
 │  Tag pointer, allocate slot
 ├─ taggedPtr = (ptr & ~7) | 0             → tier 0 (slab)
 ├─ slotIndex = 1,  highWater: 1 → 2,  generation = 0x00000001
 │
 │      Slot table after:
 │      [0] Ptr=→SlabA:cell0  Len=2   Gen=0x00000001 (live)
 │      [1] Ptr=→SlabB:cell0  Len=12  Gen=0x00000001 (live)
 │      freeHead = NoFreeSlot,  highWater = 2
 │
 └─ return PooledStringRef(pool, slotIndex=1, generation=1)
```

**State after:** 12,288 B of unmanaged memory (4,096 + 8,192) holding
28 B of actual string data. The up-front cost amortises — the next ~510
small allocations will fit into existing slabs with zero further
unmanaged allocation.

### 6.3 Allocating a large string: `pool.Allocate(doc)` (5,000 chars)

**State before:** Two slabs from §6.1–6.2, two slots used. No arena
segments yet.

```
pool.Allocate(doc)   // doc is 5,000 chars
 │
 │  Route to tier
 ├─ 5000 > 128 → arena tier
 │
 │  Compute byte count
 ├─ 5000 × 2 = 10,000 bytes (already 8-byte aligned)
 │
 │  Search existing segments
 ├─ arenaTier.Allocate(10000)
 │   ├─ segments list is empty → no segments to try
 │   │
 │   │  Create first segment
 │   ├─ capacity = max(1,048,576,  10,000) = 1,048,576  (1 MB)
 │   ├─ new SegmentedArenaSegment(1,048,576)
 │   │   ├─ Marshal.AllocHGlobal(1,048,576)
 │   │   ├─ BumpOffset = 0
 │   │   └─ binHeads[0…15] = -1  (all bins empty)
 │   ├─ segments.Add(segment)
 │   │
 │   │  Allocate within the new segment
 │   └─ segment.TryAllocate(10000)
 │       ├─ startBin = Log2(10000) - 4 = 13 - 4 = 9
 │       ├─ walk binHeads[9…15] → all -1, no free blocks
 │       ├─ bump fallback: BumpOffset(0) + 10000 ≤ 1,048,576 ✓
 │       ├─ ptr = Buffer + 0
 │       └─ BumpOffset: 0 → 10,000
 │
 │          Segment layout after:
 │          ┌────────────────────┬──────────────────────────────┐
 │          │ doc (10,000 B)     │ (unused: 1,038,576 B)        │
 │          │ live               │                    ← Bump    │
 │          └────────────────────┴──────────────────────────────┘
 │
 │  Copy 10,000 bytes of UTF-16 data into segment buffer
 │
 │  Tag the pointer
 ├─ taggedPtr = (ptr & ~7) | 1             → bit 0 = 1 (arena tier)
 │
 │  Allocate a slot
 ├─ slotIndex = 2,  highWater: 2 → 3,  generation = 0x00000001
 │
 │      Slot table after:
 │      [0] Ptr=→SlabA:cell0   Len=2     Gen=0x00000001 (slab, live)
 │      [1] Ptr=→SlabB:cell0   Len=12    Gen=0x00000001 (slab, live)
 │      [2] Ptr=→Seg0:offset0  Len=5000  Gen=0x00000001 (arena, live)
 │      freeHead = NoFreeSlot,  highWater = 3
 │
 └─ return PooledStringRef(pool, slotIndex=2, generation=1)
```

### 6.4 Allocating a very large string: `pool.Allocate(huge)` (600,000 chars)

This string is 1,200,000 bytes — larger than the default 1 MB segment.

```
pool.Allocate(huge)   // 600,000 chars = 1,200,000 bytes
 │
 ├─ 600000 > 128 → arena tier
 ├─ byteCount = 1,200,000
 │
 │  Try existing segment (Seg0, 1 MB from §6.3)
 ├─ segment.TryAllocate(1200000)
 │   ├─ walk bins → no free block ≥ 1,200,000
 │   ├─ bump: BumpOffset(10000) + 1,200,000 > 1,048,576 → doesn't fit
 │   └─ return false
 │
 │  Create oversized segment
 ├─ capacity = max(1,048,576,  1,200,000) = 1,200,000
 ├─ new SegmentedArenaSegment(1,200,000)
 │   ├─ Marshal.AllocHGlobal(1,200,000)  ← segment sized exactly for this string
 │   └─ BumpOffset = 0
 │
 │  Allocate within new segment
 ├─ ptr = Buffer + 0
 ├─ BumpOffset: 0 → 1,200,000            ← segment is now completely full
 │
 │      Segments after:
 │      segments[0] = Seg0 (1 MB,  BumpOffset=10000,   1,038,576 B available)
 │      segments[1] = Seg1 (1.2 MB, BumpOffset=1200000, 0 B available)
 │
 │      Future large allocations try Seg0 first (has space).
 │
 ├─ tag, slot allocate (slotIndex=3), return ref
 └─ return PooledStringRef(pool, slotIndex=3, generation=1)
```

### 6.5 Allocating into an existing slab (reuse, no new slab)

**State before:** SlabA (class 0, 8-char cells) has 200 free cells.
`activeSlabs[0]` → SlabA.

```
pool.Allocate("OK")   // 2 chars, same size class as §6.1
 │
 ├─ 2 ≤ 128 → slab tier
 ├─ ChooseSizeClass(2) = 0
 │
 │  Find a slab — chain head exists
 ├─ activeSlabs[0] = SlabA  →  has free cells, use it directly
 │   (no new slab created — this is the fast path)
 │
 │  Allocate a cell
 ├─ slab.TryAllocateCell()
 │   ├─ tzcnt finds first free bit → cell N
 │   ├─ flip bit to 0
 │   └─ freeCells: 200 → 199
 │
 │  Is slab now full?
 ├─ 199 > 0 → no → slab stays on chain
 │
 │      Slab chain (unchanged):
 │      activeSlabs[0] ──→ SlabA ──→ null
 │
 ├─ tag pointer, allocate slot, return ref
 └─ return PooledStringRef(pool, slotIndex=N, generation=1)
```

### 6.6 Allocating the last cell (slab fills, detaches from chain)

**State before:** SlabA (class 0) has exactly 1 free cell. Another slab
(SlabB, also class 0) has free cells behind it in the chain.

```
pool.Allocate("ab")   // 2 chars → class 0
 │
 ├─ activeSlabs[0] = SlabA (1 free cell)
 │
 │  Allocate the last cell
 ├─ slab.TryAllocateCell()
 │   ├─ tzcnt picks the last free bit
 │   └─ freeCells: 1 → 0
 │
 │  Is slab now full?
 ├─ freeCells == 0 → YES
 │   │
 │   └─ DetachHead(0)
 │       ├─ activeSlabs[0] = SlabA.NextInClass  → SlabB
 │       └─ SlabA.NextInClass = null
 │
 │          Slab chain BEFORE:
 │          activeSlabs[0] ──→ SlabA ──→ SlabB ──→ null
 │
 │          Slab chain AFTER:
 │          activeSlabs[0] ──→ SlabB ──→ null
 │          SlabA is full — off the chain, but still in allSlabs
 │
 ├─ tag pointer, allocate slot, return ref
 └─ return PooledStringRef(...)
```

SlabA is now invisible to future allocations (not on any chain), but is
still tracked in `allSlabs` so that `LocateSlabByPointer` can find it
during a future `Free`.

---

### 6.7 Freeing a small string (slab tier, slab not full)

Continuing from §6.1: freeing the `PooledStringRef` for `"Hi"`.

```
ref.Dispose()   // ref = PooledStringRef(pool, slotIndex=0, generation=1)
 │
 └─ pool.FreeSlot(slotIndex=0, generation=1)
     │
     │  Validate the slot
     ├─ disposed? → false
     ├─ slots.TryReadSlot(0, 1)
     │   ├─ slots[0].Generation = 0x00000001
     │   ├─ 0x00000001 == 1 → match ✓
     │   └─ returns { Ptr=taggedPtr, LengthChars=2, Gen=0x00000001 }
     │
     │  Decode which tier
     ├─ raw  = entry.Ptr & ~7     → raw pointer to cell 0 of SlabA
     ├─ tier = entry.Ptr & 1      → 0 (slab tier)
     │
     │  ── SLAB TIER FREE ──
     │
     │  Find the owning slab
     ├─ slabTier.LocateSlabByPointer(raw)
     │   └─ iterates allSlabs → SlabA.Contains(raw) = true → found
     │
     │  Free the cell
     ├─ slabTier.Free(raw, SlabA)
     │   ├─ wasFull = SlabA.IsFull → false (had 255 free cells)
     │   ├─ offset = raw - SlabA.Buffer = 0
     │   ├─ cellIndex = offset / 16 = 0
     │   ├─ SlabA.FreeCell(0)
     │   │   ├─ bitmap[0] |= (1UL << 0)       → bit 0 back to 1 (free)
     │   │   └─ freeCells: 255 → 256
     │   │
     │   │  Was it full before? → no
     │   └─ wasFull = false → no chain re-link needed
     │
     │      Slab chain (unchanged):
     │      activeSlabs[0] ──→ SlabA ──→ null
     │      (SlabA was already on the chain, still is)
     │
     │  Free the slot
     └─ slots.Free(0, 1)
         ├─ generation: 0x00000001 → MarkFreeAndBumpGen → 0x80000002
         │                            (free flag set, counter bumped to 2)
         ├─ slot[0].Ptr = freeHead (was NoFreeSlot)
         ├─ freeHead = 0
         └─ activeCount: N → N-1

         Slot free chain after:
         freeHead ──→ slot[0] ──→ 0xFFFFFFFF (end)
         slot[0].Generation = 0x80000002 (freed)

         Any PooledStringRef still holding generation=1 now fails:
           slots[0].Generation(0x80000002) ≠ 1 → "stale or freed"
```

**What happened to the slab?** Nothing — SlabA still exists with its full
4,096 B buffer. Cell 0 is now marked free in the bitmap and will be reused
by the next allocation in size class 0. Slab memory is never returned to
the OS until `pool.Dispose()`.

### 6.8 Freeing a small string from a full slab (re-link into chain)

**State before:** SlabA (class 0) is completely full (0 free cells). It was
detached from the chain when it filled (§6.6). SlabB is the current chain
head.

```
ref.Dispose()   // freeing a string that lives in SlabA
 │
 └─ pool.FreeSlot(slotIndex, generation)
     │
     ├─ validate slot, decode tier = 0 (slab)
     │
     │  Find the owning slab
     ├─ slabTier.LocateSlabByPointer(raw) → SlabA
     │
     │  Free the cell
     ├─ slabTier.Free(raw, SlabA)
     │   ├─ wasFull = SlabA.IsFull → TRUE (0 free cells)
     │   │
     │   ├─ SlabA.FreeCell(cellIndex)
     │   │   ├─ bitmap bit flipped back to 1
     │   │   └─ freeCells: 0 → 1
     │   │
     │   │  Was it full before? → YES → re-link!
     │   └─ LinkAtHead(0, SlabA)
     │       ├─ SlabA.NextInClass = activeSlabs[0]  → SlabB
     │       └─ activeSlabs[0] = SlabA
     │
     │          Slab chain BEFORE:
     │          activeSlabs[0] ──→ SlabB ──→ null
     │          (SlabA was full, off the chain entirely)
     │
     │          Slab chain AFTER:
     │          activeSlabs[0] ──→ SlabA ──→ SlabB ──→ null
     │          (SlabA is back — it has 1 free cell, so the invariant holds)
     │
     │  Free the slot
     └─ slots.Free(slotIndex, generation)
         └─ (same generation bump + free-chain push as §6.7)
```

The next allocation in size class 0 will find SlabA at the chain head and
use its 1 free cell via `tzcnt`. If that fills SlabA again, it will be
detached once more, and SlabB will become head again.

---

### 6.9 Freeing a large string (arena tier)

Continuing from §6.3: freeing the 5,000-char document (10,000 bytes at
offset 0 in Seg0).

```
ref.Dispose()   // ref points to slot 2, which holds the doc
 │
 └─ pool.FreeSlot(slotIndex=2, generation=1)
     │
     ├─ validate slot → match ✓
     ├─ raw  = entry.Ptr & ~7    → raw pointer into Seg0
     ├─ tier = entry.Ptr & 1     → 1 (arena tier)
     │
     │  ── ARENA TIER FREE ──
     │
     │  Find the owning segment
     ├─ arenaTier.LocateSegmentByPointer(raw)
     │   └─ iterates segments → Seg0.Contains(raw) = true → found
     │
     │  Free the block
     ├─ SegmentedArenaTier.Free(raw, byteCount=10000, Seg0)
     │   │
     │   ├─ offset = raw - Seg0.Buffer = 0
     │   ├─ size = AlignSize(10000) = 10000  (already 8-byte aligned)
     │   │
     │   │  Try coalescing with neighbours
     │   ├─ TryCoalesceForward(ref offset=0, ref size=10000)
     │   │   ├─ successor would be at offset 0 + 10000 = 10000
     │   │   ├─ 10000 >= BumpOffset(10000) → nothing beyond the bump
     │   │   └─ no coalescing
     │   │
     │   ├─ TryCoalesceBackward(ref offset=0, ref size=10000)
     │   │   ├─ looking for a free block X where X.offset + X.size == 0
     │   │   ├─ scan all 16 bins → nothing ends at offset 0
     │   │   └─ no coalescing
     │   │
     │   │  Write free-block header into the freed memory
     │   ├─ WriteHeader(offset=0, {
     │   │     SizeBytes  = 10000,
     │   │     NextOffset = -1,
     │   │     PrevOffset = -1,
     │   │     BinIndex   = Log2(10000) - 4 = 13 - 4 = 9
     │   │   })
     │   │   The first 16 bytes of what was the doc's UTF-16 data
     │   │   are now overwritten with this link header.
     │   │
     │   │  Link into the appropriate bin
     │   └─ LinkIntoBin(offset=0)
     │       ├─ binHeads[9] was -1 (empty)
     │       └─ binHeads[9] = 0
     │
     │          Segment bins after:
     │          binHeads[0…8]  = -1
     │          binHeads[9]    = 0   ←── free block: 10,000 B at offset 0
     │          binHeads[10…15] = -1
     │
     │          Segment layout after:
     │          ┌──────────────────────┬────────────────────────────┐
     │          │ FREE BLOCK (10,000 B)│ (unused: 1,038,576 B)      │
     │          │ header + garbage     │                  ← Bump    │
     │          └──────────────────────┴────────────────────────────┘
     │          BumpOffset still 10,000 — bump continues from there.
     │
     │  Free the slot
     └─ slots.Free(2, 1)
         └─ generation bumped, slot pushed onto free chain

         A future 5,000–8,191 char allocation (10,000–16,383 bytes)
         would find this block via bin 9 and reuse it directly.
```

**What happened to the segment?** Nothing — Seg0 still exists (1 MB of
unmanaged memory). The freed region is now a free block threaded into
bin 9. The string's UTF-16 data is gone — the first 16 bytes are a link
header, the rest are garbage.

### 6.10 Freeing adjacent arena blocks (coalescing)

**Setup:** A segment has three consecutive allocations, then we free them
in a deliberate order to show coalescing.

```
Segment state: three live blocks, BumpOffset = 3584

┌──────────────┬──────────────────┬────────────┬───────────────────┐
│ A (1,024 B)  │ B (2,048 B)      │ C (512 B)  │ (unused)          │
│ offset 0     │ offset 1024      │ offset 3072│        ← Bump     │
│ live         │ live             │ live       │                   │
└──────────────┴──────────────────┴────────────┴───────────────────┘

binHeads[0…15] = -1  (no free blocks)
```

**Step 1: Free B (middle block)**

```
Free B (2,048 B at offset 1024)
 │
 ├─ TryCoalesceForward: is there a free block at 1024 + 2048 = 3072?
 │   └─ scan all bins → no free block at offset 3072 → nothing
 │
 ├─ TryCoalesceBackward: is there a free block ending at 1024?
 │   └─ scan all bins → no block X where X.offset + X.size == 1024 → nothing
 │
 ├─ WriteHeader(1024, { SizeBytes=2048, BinIndex=Log2(2048)-4=11-4=7 })
 └─ LinkIntoBin(1024) → binHeads[7] = 1024

 ┌──────────────┬──────────────────┬────────────┬───────────────────┐
 │ A (1,024 B)  │ B FREE (2,048 B) │ C (512 B)  │ (unused)          │
 │ live         │ hdr+garbage      │ live       │                   │
 └──────────────┴──────────────────┴────────────┴───────────────────┘
 binHeads[7] ──→ @1024 (2048 B, prev=-1, next=-1)
```

**Step 2: Free A (left block — coalesces forward into B)**

```
Free A (1,024 B at offset 0)
 │
 ├─ TryCoalesceForward: is there a free block at 0 + 1024 = 1024?
 │   ├─ scan bins → found! B is at offset 1024 in bin 7
 │   ├─ UnlinkFromBin(1024)  →  binHeads[7] = -1  (B removed)
 │   └─ size = 1024 + 2048 = 3072  (A absorbs B)
 │
 ├─ TryCoalesceBackward: is there a free block ending at 0?
 │   └─ nothing ends at offset 0 → no
 │
 ├─ WriteHeader(0, { SizeBytes=3072, BinIndex=Log2(3072)-4=11-4=7 })
 └─ LinkIntoBin(0) → binHeads[7] = 0

 ┌─────────────────────────────────┬────────────┬───────────────────┐
 │ A+B COALESCED FREE (3,072 B)    │ C (512 B)  │ (unused)          │
 │ hdr at offset 0, size 3072      │ live       │                   │
 └─────────────────────────────────┴────────────┴───────────────────┘
 binHeads[7] ──→ @0 (3072 B, prev=-1, next=-1)

 Without coalescing, two adjacent free blocks of 1,024 + 2,048 could
 never satisfy a single 2,500-byte request. Now they can.
```

**Step 3: Free C (right block — coalesces backward into A+B)**

```
Free C (512 B at offset 3072)
 │
 ├─ TryCoalesceForward: is there a free block at 3072 + 512 = 3584?
 │   └─ 3584 >= BumpOffset(3584) → past the bump, nothing there
 │
 ├─ TryCoalesceBackward: is there a free block ending at 3072?
 │   ├─ scan bins → found! A+B at offset 0, size 3072
 │   │   0 + 3072 == 3072 ✓
 │   ├─ UnlinkFromBin(0)  →  binHeads[7] = -1  (A+B removed)
 │   ├─ offset = 0  (adopt A+B's starting offset)
 │   └─ size = 3072 + 512 = 3584  (everything merged)
 │
 ├─ WriteHeader(0, { SizeBytes=3584, BinIndex=Log2(3584)-4=11-4=7 })
 └─ LinkIntoBin(0) → binHeads[7] = 0

 ┌─────────────────────────────────────────────┬───────────────────┐
 │ A+B+C ALL COALESCED FREE (3,584 B)          │ (unused)          │
 │ single free block, offset 0, size 3584      │        ← Bump     │
 └─────────────────────────────────────────────┴───────────────────┘
 binHeads[7] ──→ @0 (3584 B, prev=-1, next=-1)
 BumpOffset = 3584 (unchanged — bump never moves backwards)
```

The entire used portion of the segment is now one contiguous free block.
A future allocation ≤ 3,584 B will reuse this space directly without
advancing the bump pointer.

### 6.11 Arena allocation reusing a free block (with split)

**State before:** Seg0 has one free block of 10,000 B at offset 0 (from
§6.9) on bin 9. `BumpOffset = 10000`.

```
pool.Allocate(shortDoc)   // 200 chars = 400 bytes
 │
 ├─ 200 > 128 → arena tier
 ├─ byteCount = 400,  AlignSize(400) = 400  (already aligned)
 │
 │  Search bins
 ├─ startBin = Log2(400) - 4 = 8 - 4 = 4
 ├─ walk binHeads[4…8] → all -1
 ├─ binHeads[9] = 0 → free block at offset 0
 │   │
 │   ├─ ReadHeader(0) → { SizeBytes=10000 }
 │   ├─ 10000 >= 400 ✓ → use this block
 │   │
 │   │  Unlink the block from bin 9
 │   ├─ UnlinkFromBin(0) → binHeads[9] = -1
 │   │
 │   │  Split: remainder = 10000 - 400 = 9600 bytes (≥ 16, so split)
 │   ├─ tailOffset = 0 + 400 = 400
 │   ├─ WriteHeader(400, {
 │   │     SizeBytes  = 9600,
 │   │     BinIndex   = Log2(9600) - 4 = 13 - 4 = 9
 │   │   })
 │   ├─ LinkIntoBin(400) → binHeads[9] = 400
 │   │
 │   │  Return pointer to the taken portion
 │   └─ ptr = Buffer + 0
 │
 │      Segment layout after:
 │      ┌──────────┬─────────────────────┬─────────────────────────┐
 │      │ shortDoc │ FREE (9,600 B)      │ (unused: 1,038,576 B)   │
 │      │ 400 B    │ hdr at offset 400   │                ← Bump   │
 │      │ live     │ on bin 9            │                         │
 │      └──────────┴─────────────────────┴─────────────────────────┘
 │      binHeads[9] ──→ @400 (9600 B)
 │
 ├─ tag pointer (tier=1), allocate slot, return ref
 └─ return PooledStringRef(...)
```

The 10,000 B free block was split: 400 B taken for the new string, 9,600 B
remainder re-linked as a new (smaller) free block in the same bin.

### 6.12 Allocating into a reused slot (free chain pop)

**State before:** Slots 0 and 2 have been freed. The slot free chain is
`freeHead → 2 → 0 → NoFreeSlot`. `highWater = 4`. Slot 1 and 3 are live.

```
pool.Allocate("reuse")   // 5 chars → slab tier, class 0
 │
 │  (slab tier allocation proceeds as normal — omitted for brevity)
 │
 │  Allocate a slot
 ├─ slots.Allocate(taggedPtr, lengthChars=5)
 │   │
 │   │  freeHead ≠ NoFreeSlot → reuse a freed slot instead of bumping highWater
 │   │
 │   ├─ slotIndex = freeHead = 2
 │   ├─ freeHead = (uint)slots[2].Ptr = 0   ← pop: follow the chain link
 │   │
 │   │      Slot free chain BEFORE:
 │   │      freeHead ──→ slot[2] ──→ slot[0] ──→ 0xFFFFFFFF (end)
 │   │
 │   │      Slot free chain AFTER:
 │   │      freeHead ──→ slot[0] ──→ 0xFFFFFFFF (end)
 │   │      (slot 2 is no longer on the chain — it's live now)
 │   │
 │   ├─ generation = ClearFreeAndBumpGen(0x80000002) = 0x00000003
 │   │   (free flag cleared, counter bumped from 2 → 3)
 │   │
 │   ├─ slot[2] = { Ptr=taggedPtr, LengthChars=5, Generation=0x00000003 }
 │   └─ activeCount: 2 → 3
 │
 │      Slot table after:
 │      [0] Gen=0x80000002 (freed, on chain)
 │      [1] Gen=0x00000001 (live)
 │      [2] Gen=0x00000003 (live — just reused!)
 │      [3] Gen=0x00000001 (live)
 │      freeHead = 0,  highWater = 4 (unchanged — no new slots consumed)
 │
 └─ return PooledStringRef(pool, slotIndex=2, generation=3)
```

The slot was reused without growing the slot table. Any old
`PooledStringRef` that still holds `slotIndex=2, generation=1` (the
original allocation) will fail the generation check — `0x00000003 ≠ 1`.

### 6.13 Slot table growth (doubling)

**State before:** Slot table has `Capacity = 64`, `highWater = 64` (every
slot has been touched at least once), `freeHead = NoFreeSlot` (no freed
slots available). All 64 slots are live.

```
pool.Allocate("grow")   // triggers slot table growth
 │
 │  (tier allocation proceeds as normal — omitted)
 │
 │  Allocate a slot
 ├─ slots.Allocate(taggedPtr, lengthChars=4)
 │   │
 │   ├─ freeHead = NoFreeSlot → no reusable slots
 │   ├─ highWater(64) == Capacity(64) → must grow!
 │   │   │
 │   │   └─ Grow()
 │   │       ├─ newCapacity = 64 × 2 = 128
 │   │       └─ Array.Resize(ref slots, 128)
 │   │           Allocates a new SegmentedSlotEntry[128] on the managed heap,
 │   │           copies all 64 existing entries, slots[64…127] are zeroed.
 │   │           Old array becomes eligible for GC.
 │   │
 │   │           This is the ONLY managed allocation that occurs during
 │   │           steady-state pool usage. It happens at powers of 2:
 │   │           64 → 128 → 256 → 512 → …
 │   │
 │   ├─ slotIndex = 64,  highWater: 64 → 65
 │   ├─ generation = ClearFreeAndBumpGen(0x00000000) = 0x00000001
 │   └─ Capacity is now 128 — next 63 allocations won't trigger growth
 │
 └─ return PooledStringRef(pool, slotIndex=64, generation=1)
```

Growth is O(n) in current slot count due to the array copy, but it
happens only at doubling boundaries — amortised O(1) per allocation.
The raw unmanaged pointers stored in slots remain valid because
neither tier ever moves allocated memory.

### 6.14 pool.Clear() — reset without freeing memory

**State before:** Pool has been in use. 3 slabs exist (SlabA in class 0,
SlabB in class 1, SlabC in class 0 — SlabC is full and off its chain).
2 segments exist. 50 slots are live, 10 are on the free chain.
`highWater = 60`.

```
pool.Clear()
 │
 │  ── STEP 1: Clear all slots ──
 │
 ├─ slots.ClearAllSlots()
 │   │
 │   │  Walk slots[0…59] (everything below highWater):
 │   ├─ for each slot:
 │   │   ├─ if live → MarkFreeAndBumpGen (set free flag, bump counter)
 │   │   ├─ if already freed → leave generation as-is
 │   │   ├─ slot.Ptr = next index (i+1), or NoFreeSlot for the last one
 │   │   └─ slot.LengthChars = 0
 │   │
 │   │  Rebuild the free chain in index order:
 │   ├─ freeHead = 0
 │   ├─ slot[0].Ptr → 1 → slot[1].Ptr → 2 → … → slot[59].Ptr → 0xFFFFFFFF
 │   └─ activeCount = 0
 │
 │      Slot free chain after:
 │      freeHead ──→ 0 ──→ 1 ──→ 2 ──→ … ──→ 59 ──→ 0xFFFFFFFF
 │      highWater = 60 (unchanged — doesn't shrink)
 │      Every slot has its free flag set. All old PooledStringRefs are now
 │      stale — their generation will mismatch on any read attempt.
 │
 │  ── STEP 2: Reset slab tier ──
 │
 ├─ slabTier.ResetAll()
 │   │
 │   │  Phase 1: Disconnect all chains and reset bitmaps
 │   ├─ activeSlabs[0…4] = null              ← all chain heads cleared
 │   ├─ for each slab in allSlabs:
 │   │   ├─ slab.ResetAllCellsFree()
 │   │   │   ├─ bitmap words all set to ulong.MaxValue  (every cell free)
 │   │   │   ├─ excess bits past CellCount cleared
 │   │   │   └─ freeCells = CellCount
 │   │   └─ slab.NextInClass = null          ← unlink from any chain
 │   │
 │   │  Phase 2: Re-thread every slab into its size-class chain
 │   └─ for each slab in allSlabs:
 │       └─ LinkAtHead(SizeClassForCellBytes(slab.CellBytes), slab)
 │
 │          Slab chains BEFORE:
 │          activeSlabs[0] ──→ SlabA ──→ null       (SlabC was full, off chain)
 │          activeSlabs[1] ──→ SlabB ──→ null
 │
 │          Slab chains AFTER:
 │          activeSlabs[0] ──→ SlabC ──→ SlabA ──→ null   (SlabC is back!)
 │          activeSlabs[1] ──→ SlabB ──→ null
 │
 │          Note: allSlabs order is [SlabA, SlabB, SlabC] (insertion order).
 │          Phase 2 iterates this order, calling LinkAtHead each time.
 │          LinkAtHead prepends, so the last slab processed for a class
 │          ends up at the chain head. SlabC (processed after SlabA for
 │          class 0) becomes the new head.
 │
 │          All 3 slabs have full bitmaps (every cell free).
 │          Unmanaged memory is NOT freed — buffers are reused.
 │
 │  ── STEP 3: Reset arena tier ──
 │
 └─ arenaTier.ResetAll()
     │
     └─ for each segment in segments:
         └─ segment.Reset()
             ├─ BumpOffset = 0               ← as if nothing was ever allocated
             └─ binHeads[0…15] = -1          ← free lists discarded
 
             Segment layout BEFORE:
             ┌────────────────┬──────────┬──────────────────────────┐
             │ live blocks    │ free blk │ (unused)       ← Bump    │
             └────────────────┴──────────┴──────────────────────────┘
 
             Segment layout AFTER:
             ┌──────────────────────────────────────────────────────┐
             │ (entire buffer available)                  ← Bump=0  │
             └──────────────────────────────────────────────────────┘
 
             The old string data is still physically in the buffer but
             will be overwritten by future bump allocations. No free-list
             entries exist because BumpOffset = 0 means the bump allocator
             covers the full capacity.
 
             Unmanaged memory is NOT freed — segments are reused.
```

**Clear vs Dispose:**

| | `Clear()` | `Dispose()` |
|-|-----------|-------------|
| Slots | All marked freed, chain rebuilt | All marked freed, chain rebuilt |
| Slab bitmaps | Reset to all-free | Not explicitly reset |
| Slab memory | **Kept** — buffers reused | **Freed** via `Marshal.FreeHGlobal` |
| Arena bumps/bins | Reset to zero | Not explicitly reset |
| Arena memory | **Kept** — buffers reused | **Freed** via `Marshal.FreeHGlobal` |
| Pool usable after? | Yes | No — `disposed = true` |

`Clear()` is for "throw away all strings but keep the pool warm for the
next batch." `Dispose()` is for "we're done, return everything to the OS."

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
