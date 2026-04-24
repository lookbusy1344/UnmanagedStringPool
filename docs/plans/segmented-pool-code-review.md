# SegmentedStringPool — Code Review & Remediation Plan

Scope: `SegmentedStringPool`, `SegmentedSlotTable`, `SegmentedSlotEntry`, `SegmentedSlabTier`, `SegmentedSlab`, `SegmentedArenaTier`, `SegmentedArenaSegment`, `PooledStringRef`. Legacy `UnmanagedStringPool` / `PooledString` are out of scope.

Findings are grouped by severity. Each item names the affected file(s), the observed problem, and a concrete remediation. Apply TDD per project CLAUDE.md: write the failing test first, then the fix.

---

## P0 — Correctness / API bugs

### P0-1. Configuration record is unreachable
`SegmentedStringPool.cs:25-59` — `SegmentedStringPoolOptions` is a public record, but the only constructor that accepts it is **private**. The only public constructor is parameterless. Every call site (`Demo`, `Tests`, `Benchmarks`) uses defaults; there is currently no way to tune `InitialSlotCapacity`, `SlabCellsPerSlab`, `ArenaSegmentBytes`, or `SmallStringThresholdChars`.

**Remediation**: promote the options constructor to `public`. Add a test that constructs a pool with non-default options and asserts routing (e.g. `SmallStringThresholdChars=4` forces a 5-char string to the arena tier).

### P0-2. Arena splitter permanently loses < 16 B of slack per split
`SegmentedArenaSegment.cs:66-102` — when a free block is split and the trailing remainder is `< MinArenaBlockBytes` (16 B), the remainder is handed to the caller as part of the allocation but never recorded anywhere. On `Free`, the caller supplies its original `byteCount`, `AlignSize` rounds that up to the aligned request size, and the block is linked into the bin sized for the *request*, not the block actually handed out. The 0–15 B slack is orphaned until `Reset()` / `Clear()`.

This is cumulative: every split with a sub-threshold remainder bleeds capacity forever. Over many alloc/free cycles this shows up as "arena never coalesces back to the full segment even though nothing is live".

**Remediation options (pick one — the choice affects memory vs. CPU trade-off):**
- (a) **Round request up to the free block's full size when remainder < min.** Store the real allocated size in a small header *before* the returned pointer (8 B prefix: `SizeBytes`). `Free` reads the header, freeing the true extent. Adds 8 B overhead per arena allocation but eliminates slack.
- (b) **Never hand out oversized blocks.** If remainder < min, skip this bin entry and keep searching. Leaves zombie fragments in bins until coalesced, but keeps the 0-overhead payload layout.
- (c) **Track per-allocation size in the slot table** by widening `SegmentedSlotEntry` to include the actual bytes granted. Pairs well with P2-1 below.

Recommend (c) — it synergises with moving the owning-slab/segment reference into the slot entry (see P1-1) and already touches that type.

### P0-3. `PooledStringRef.Equals(object)` + `GetHashCode()` violate the hash contract when compared to `string`
`PooledStringRef.cs:191-195, 200-219` — `Equals(object? obj)` returns true for a `string s` whose content matches. But `GetHashCode()` samples only prefix+suffix+length, while `string.GetHashCode()` uses the CLR string hasher. A `Dictionary<object, T>` keyed by both types will fail to locate equal values.

**Remediation**: either (a) drop the `string s` branch from `Equals(object)` — cross-type equality is not required by `IEquatable<PooledStringRef>`, and callers wanting content comparison can call `AsSpan().SequenceEqual(s)` explicitly; or (b) override `GetHashCode` to use `string.GetHashCode(ReadOnlySpan<char>)` so both types hash consistently. Option (a) is simpler and matches the documented "content-based equality" among refs.

### P0-4. `SegmentedArenaSegment.Next` is dead code
`SegmentedArenaSegment.cs:51` — `public SegmentedArenaSegment? Next { get; set; }` has no reader and no writer across the codebase. Either the segment list was intended to become an intrusive chain (mirroring `SegmentedSlab.NextInClass`) and never finished, or the property was left over from a refactor.

**Remediation**: delete the property. If a future change wants an intrusive chain, add it back with tests.

### P0-5. `Insert` / `Replace` leak rented arrays on `Allocate` throw
`PooledStringRef.cs:82-109, 111-184` — both methods rent from `ArrayPool<char>.Shared`, call `Pool.Allocate(buffer)`, then return the rented buffer. If `Allocate` throws (e.g. `ObjectDisposedException` from a racing Dispose, or OOM from a slot-table grow), the rented arrays are never returned to the pool.

**Remediation**: wrap the `Allocate` + return-to-pool section in `try { … } finally { if (rented is not null) ArrayPool<char>.Shared.Return(rented); }`. Same for `rentedMatches` in `Replace`.

### P0-6. `PooledStringRef` has a public constructor
`PooledStringRef.cs:18-23` — callers can forge refs with arbitrary `(Pool, SlotIndex, Generation)`. A forged ref with a valid-looking generation will pass `TryReadSlot` and read (or free!) whatever is at that slot. This also breaks the invariant that every live `PooledStringRef` came from `Allocate`.

**Remediation**: make the constructor `internal`. Add `InternalsVisibleTo` for the test assembly if needed (already in place for other tests).

---

## P1 — Performance / architectural

### P1-1. Free path is O(allSlabs) or O(segments) per call
`SegmentedSlabTier.cs:111-120` and `SegmentedArenaTier.cs:73-82` — on every `Free`, the pool iterates all slabs (or all segments) doing range checks to find the owner. For a pool with thousands of slabs, `FreeSlot` becomes linear in pool size.

**Remediation**: store the owning slab/segment reference (or an index into the `allSlabs` / `segments` list) directly in `SegmentedSlotEntry`. Ideas:
- Widen `SegmentedSlotEntry` by 8 bytes to hold an owner ref — cheap because the type is already cache-line-friendly and the rest of the allocator makes many more memory accesses per op than one extra field reads.
- Or pack a 16-bit owner index into the currently-unused high bits of `LengthChars` (cap small strings at 65535 chars is fine; the constant threshold is 128). Loses 16 bits from `LengthChars`, making the length field effectively a short; acceptable if arena allocations are similarly capped, otherwise mark length as 32-bit-char-count and index as 16-bit-owner with explicit packing.

Either approach turns Free into O(1) without changing the public contract.

### P1-2. Arena coalescing is O(binCount × freeBlocks)
`SegmentedArenaSegment.cs:215-255` — `TryCoalesceForward` and `TryCoalesceBackward` scan every bin linearly. On a fragmented segment this is non-trivial.

**Remediation**: add **boundary tags** — a matching footer at the end of every free block containing its size and free flag. `TryCoalesceBackward` then reads the 4 B immediately before `offset` to check if the prior block is free. `TryCoalesceForward` already knows the successor offset via `offset + size`, so one direct header read there. Both become O(1). The boundary-tag footer adds 8 B overhead per free block (not per allocation), which is acceptable; an in-use block can skip the footer since coalescing only reads it on free-neighbour blocks.

### P1-3. `SegmentedSlabTier.Allocate` recovery path corrupts chain invariant
`SegmentedSlabTier.cs:82-94` — when the chain-head slab surprises us by failing `TryAllocateCell`, we allocate a new slab but never detach the full head. `AllocateNewSlab` prepends the new slab, so the full slab is now the *second* node of the chain. When the new head fills and is detached, the next `Allocate` will hit the full slab, fail again, and allocate yet another slab. This is a latent leak under the "shouldn't happen" branch.

**Remediation**: when the head unexpectedly fails, detach it first, *then* allocate. Either way add a test that drives this path by mutating a slab's bitmap to a full state and calling `Allocate` twice to confirm the chain stays clean.

### P1-4. Slot table never shrinks
`SegmentedSlotTable.cs:136-144` — `Grow` doubles the array but there is no `Shrink`. A pool that peaks at 10M allocations and drops back to 100 keeps a 10M-entry (~160 MB) managed array forever.

**Remediation**: add a `MaybeShrink` hook inside `Free` / `ClearAllSlots` that halves the array when `ActiveCount < Capacity / 4` and `Capacity > InitialCapacity`. Decide with a simple hysteresis to avoid thrashing.

### P1-5. `Reserve(int chars)` splits 50/50 regardless of workload
`SegmentedStringPool.cs:161-172` — the even split is arbitrary. Workloads that are mostly small or mostly large waste half the reservation.

**Remediation**: expose `ReserveSmall(int chars)` and `ReserveLarge(int chars)` as separate public methods. Keep `Reserve(int)` as a convenience that calls both with the current 50/50 split, or deprecate it.

### P1-6. `SegmentedSlabTier.Reserve` always uses size-class 4 (128-char cells)
`SegmentedSlabTier.cs:149-156` — even if the caller only ever allocates 8-char strings, pre-allocated slabs are all the largest class. Small-string allocations will trigger fresh slab creation on first use.

**Remediation**: take a size-class hint on `Reserve`, or pre-allocate one slab per size class proportional to reserved chars.

### P1-7. `SegmentedStringPool.TotalBytesManaged` under-reports
`SegmentedStringPool.cs:63` — reports only `slots.Capacity * 16`. Ignores the `allSlabs`/`segments` lists, their headers, the bitmap arrays in each slab, and the slab/segment class headers. The published metric is misleading.

**Remediation**: sum the actual managed overhead (`slots.Capacity * Unsafe.SizeOf<SegmentedSlotEntry>()` + slab/segment count × per-instance managed footprint + sum of `bitmap.Length * 8` across slabs). Add a test against expected values.

---

## P2 — Robustness / hardening

### P2-1. `Clear()` iterates all slots even when `ActiveCount == 0`
`SegmentedSlotTable.cs:114-130` — the rebuild walks every slot up to `highWater`, even on a freshly-cleared table. After a heavy workload this is O(highWater) with no active slots to visit.

**Remediation**: short-circuit when `ActiveCount == 0 && freeHead != NoFreeSlot && every slot below highWater is already threaded into the free chain`. Alternatively, track `highWater` separately from "first dirty slot" so `Clear` on an already-clean table is O(1).

### P2-2. `SegmentedSlab.Dispose(bool)` has no disposal check on subsequent calls
`SegmentedSlab.cs:148-154` — if `Dispose()` is called, then some other code path (via `SegmentedSlabTier.LocateSlabByPointer` → `Contains`) calls into the slab, the slab's `Buffer` has been freed but the field holds a dangling pointer. Callers get "successful" range-check results and may write into freed memory.

**Remediation**: add an `ObjectDisposedException.ThrowIf(disposed, this)` at the entry of `Contains`, `TryAllocateCell`, `FreeCell`, `OffsetOfCell`, `CellIndexFromOffset`, `ResetAllCellsFree`. Same pattern in `SegmentedArenaSegment`.

### P2-3. `FreeSlot` is not atomic — a tier-free failure leaves the slot live
`SegmentedStringPool.cs:123-142` — if `slabTier.Free` or `SegmentedArenaTier.Free` throws (e.g. cell already free, indicating internal corruption), the slot is never freed, so the slot index is leaked and any subsequent re-read would dereference into an inconsistent allocator.

**Remediation**: catch and log allocator exceptions (they signal corruption; the pool is unusable regardless), free the slot anyway, and rethrow after the slot is freed. Alternatively mark the pool as `faulted` and throw on subsequent operations — fail-loud is fine here.

### P2-4. Finalizer ordering relies on GC to collect slabs/segments after pool
`SegmentedStringPool.cs:180-192` — `Dispose(false)` (finalizer path) intentionally skips `slabTier.Dispose()` / `arenaTier.Dispose()` because those fields may already be finalized. Each `SegmentedSlab` / `SegmentedArenaSegment` has its own finalizer that frees its `Buffer`, so memory does get reclaimed — but only on the next GC cycle. In long-running processes leaking disposal is a real leak.

**Remediation**: this is a best-practice pattern, not strictly a bug. Add an analyzer-enforced warning that `SegmentedStringPool` must be disposed (CA2000 / custom analyzer), and consider a `Debug.Fail` in the finalizer to surface leaks in debug builds.

### P2-5. `Replace` rent-on-doubling strategy churns `ArrayPool`
`PooledStringRef.cs:124-148` — when matches exceed 64, the method rents a new int array for every doubling (64→128→256…), copying the previous contents and returning the prior rental. For high-match-density cases this causes ArrayPool churn.

**Remediation**: rent once at `Math.Max(ReplaceInlineMatchCap, source.Length / oldValue.Length + 1)` (an upper bound on possible matches). One rent, one return. Replace the doubling strategy.

### P2-6. `Insert` / `Replace` have no overflow check on `totalLength`
`PooledStringRef.cs:94, 157` — `original.Length + value.Length` and `source.Length + matchCount * (newValue.Length - oldValue.Length)` can overflow `int` when strings approach `int.MaxValue`. Unlikely in practice, but the crash mode is silent (negative `stackalloc` size → runtime exception that's hard to trace).

**Remediation**: use `checked { … }` around the arithmetic, or explicit `long` intermediates with a range check and a descriptive `OverflowException`.

### P2-7. `SegmentedSlab` ctor / `ResetAllCellsFree` duplicate bitmap-init logic
`SegmentedSlab.cs:35-44, 120-131` — identical 9-line bitmap initialisation appears twice. Cheap refactor win; reduces risk of the two diverging.

**Remediation**: extract a private `FillBitmapAllFree()` helper.

### P2-8. `SegmentedArenaTier.Allocate` picks the first-fit segment by iteration order
`SegmentedArenaTier.cs:43-60` — the loop accepts the first segment that returns `true` from `TryAllocate`. When a big allocation created a dedicated oversized segment, subsequent small allocations will still flow into that segment once earlier ones fill up, mixing sizes and defeating the "dedicated oversized" intent.

**Remediation**: flag dedicated oversized segments (`IsOversized` set at creation when `byteCount > defaultSegmentBytes`) and skip them from the normal allocation loop unless the incoming request is also oversized. Or allocate sequentially from the tail, falling back to earlier segments only for bump exhaustion.

### P2-9. `SegmentedStringPool.ctor` doesn't validate threshold fits the slab size classes
`SegmentedStringPool.cs:52-59` — `SmallStringThresholdChars` above 128 will cause `SegmentedSlabTier.Allocate` to throw because `ChooseSizeClass` returns -1. Silently wrong default-path routing until the first oversized small string.

**Remediation**: validate `SmallStringThresholdChars <= 128` in the ctor, or have `AllocateUnmanaged` route anything above 128 to the arena tier regardless of the threshold.

---

## Disposal & finalizer audit

**Overall verdict: the dispose pattern is implemented correctly.** Each class that directly owns `Marshal.AllocHGlobal`-backed memory (`SegmentedSlab`, `SegmentedArenaSegment`) has both `Dispose` and a finalizer; each wrapper that owns only managed references (`SegmentedSlabTier`, `SegmentedArenaTier`, `SegmentedStringPool`) has `Dispose` but correctly omits a finalizer for the buffer cleanup, delegating to the per-instance finalizers. `PooledStringRef` is a struct (no finalizer possible) and makes its `Dispose` idempotent through the slot-generation check.

Concrete issues worth fixing:

### D-1. `disposed = true` set *after* `Marshal.FreeHGlobal`
`SegmentedSlab.cs:148-154`, `SegmentedArenaSegment.cs:142-148` — if `FreeHGlobal` throws (rare: invalid handle, AccessViolationException), `disposed` stays false. A subsequent finalizer run would attempt to free the same buffer again. Hardening, not a live bug.

**Remediation**: reorder to `disposed = true;` first, then `Marshal.FreeHGlobal(Buffer);`.

### D-2. Tier classes have no `disposed` flag
`SegmentedSlabTier.cs:158-168`, `SegmentedArenaTier.cs:109-116` — `Dispose` iterates and clears the list but sets no flag. Calling `Allocate` or `Reserve` on a disposed tier directly would happily create fresh slabs/segments, silently resurrecting unmanaged memory without the pool's knowledge. Currently shielded because every entry point flows through `SegmentedStringPool.disposed`.

**Remediation**: add a private `disposed` flag, guard every public method with `ObjectDisposedException.ThrowIf(disposed, this)`, and set it in `Dispose`. Cheap and prevents future refactors from exposing the hazard.

### D-3. Pool finalizer skips tier cleanup — works today, fragile tomorrow
`SegmentedStringPool.cs:180-192` — the `Dispose(false)` path deliberately skips `slabTier.Dispose()` / `arenaTier.Dispose()`. This is convention-correct because tiers currently own only managed state, and the per-slab / per-segment finalizers reclaim the unmanaged buffers independently. But if a future tier refactor introduces direct unmanaged state (caches, pinned buffers, etc.), the pool's finalizer path will silently miss it.

**Remediation**: add a code comment at the `if (disposing)` block explicitly stating the invariant "the tier classes must not own unmanaged memory directly — each slab/segment is independently finalizable". If the invariant ever changes, the finalizer path needs re-examination.

### D-4. Finalizer-based cleanup of leaked pools is silent
If a `SegmentedStringPool` is never disposed, unmanaged memory is still reclaimed via GC finalization of each slab/segment, but with no signal to the developer. Long-running workloads may retain hundreds of MB longer than necessary.

**Remediation**: add a `Debug.Fail` (or `Debug.WriteLine`) inside `~SegmentedStringPool` when `!disposed` to surface leaks in debug builds. Do not throw from a finalizer.

### D-5. Disposal during active `PooledStringRef.Free` race
`SegmentedStringPool.cs:123-125` — `FreeSlot` reads `disposed` without a memory barrier. Because `disposed` isn't `volatile`, a concurrent `pool.Dispose()` on another thread may not be observed. The pool's contract is explicitly single-threaded so this is by design — but finalization happens on a GC thread, so the `~SegmentedStringPool` path could race with user-thread `PooledStringRef.Free`.

**Remediation**: either mark `disposed` as `volatile` (cheap, removes the theoretical window) or tighten the contract documentation to state that `PooledStringRef.Free`/`Dispose` must not be called across threads relative to pool finalization.

### D-6. `PooledStringRef.Free` is semantically correct but non-obvious
`PooledStringRef.cs:39` — `Free` → `Pool?.FreeSlot` → `TryReadSlot` fails on stale generation → silent no-op. This handles double-dispose, post-pool-dispose, and forged-ref cases uniformly. Worth a doc comment making the idempotency guarantee explicit rather than leaving it implicit in the generation machinery.

---

## P3 — Style / minor

### P3-1. `ReadHeader` / `WriteHeader` bypass the `unsafe` convention
`SegmentedArenaSegment.cs:172-176` — `unsafe` methods exposing raw pointer arithmetic. Consider `Unsafe.Read<SegmentedFreeBlockHeader>((void*)(Buffer + offset))` / `Unsafe.Write` which is idiomatic and doesn't require fields to be `unsafe`.

### P3-2. `SegmentedSlab.Contains` / `SegmentedArenaSegment.Contains` are near-identical
Extract a small helper or a static extension over `IntPtr` + `capacity`.

### P3-3. `PooledStringRef.IsEmpty` triple-check is defensive but documented-only
`PooledStringRef.cs:31-33` — the comment explains why all three fields are checked, but the allocator never produces a non-null Pool with Generation=0 (generation is bumped before returning). Consider `Pool is null` alone for clarity, keep the comment documenting why Generation≥1 is the invariant.

### P3-4. `SegmentedStringPool.TotalBytesManaged` is an estimate, not a fact
If P1-7 isn't actioned, at least rename this to `EstimatedBytesManaged` or add a doc comment clarifying scope.

### P3-5. `SegmentedArenaSegment.BumpOffset` is public mutable
`SegmentedArenaSegment.cs:35` — callers outside the class can mutate the bump frontier. Should be `public int BumpOffset { get; private set; }`. (Only accessed internally for tests? If so, make it `internal` and expose via test hooks.)

---

## Suggested sequencing

Do these in order; each step is independently mergeable, with tests first per project policy.

1. **P0-1** (trivial; restores documented configuration API) — 10 min + a test.
2. **P0-4** (delete dead code) — 5 min.
3. **P0-6** (constructor visibility) — 15 min, check tests still compile.
4. **P0-5** (ArrayPool leak in Insert/Replace) — 30 min including a test that throws mid-Allocate.
5. **P0-3** (Equals/GetHashCode contract) — 20 min; pick option (a) unless there's a strong case for cross-type hashing.
6. **P2-9** (ctor validation) — 15 min.
7. **P0-2** (arena slack loss) — bundle with **P1-1** (Free O(1)) since both want `SegmentedSlotEntry` widened. Meaningful half-day with tests for fragmentation under stress.
8. **P1-2** (boundary tags for coalescing) — half-day; adds a microbench to confirm improvement.
9. **P1-4** (slot table shrink) — 1 hr + hysteresis test.
10. **P1-5** / **P1-6** (Reserve API) — 1 hr; decide on API shape first.
11. Remaining P2 items as opportunity arises.

Every commit must pass `dotnet build && dotnet format && gtimeout 120 dotnet test` per CLAUDE.md.

---

## Decisions the team should take before P0-2 / P1-1

Two items affect the slot entry layout and are worth settling together:

- **Slot entry width**: 16 B (current) vs. 24 B (add 8 B owner ref, gains O(1) free and slack recovery).
- **Slack recovery strategy**: store allocated bytes in slot entry (favours the 24 B path) vs. 8 B prefix header per arena allocation (touches allocation-time layout, keeps slot entry small).

Recommendation: widen the slot entry. The managed footprint of the slot table is `slots.Capacity * sizeof(entry)` — going from 16 to 24 B on a 1 M-entry table costs 8 MB of managed heap for a permanent CPU win on every Free. Worth it.
