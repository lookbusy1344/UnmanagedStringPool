# Project Overview

.NET 10.0 unmanaged string pool reducing GC pressure. The primary implementation is `SegmentedStringPool`; `UnmanagedStringPool` is the older single-block implementation, retained for reference.

## Build and Test

```bash
dotnet build
dotnet test
dotnet test --logger:"console;verbosity=detailed"
dotnet test --filter "FullyQualifiedName~ClassName.MethodName"
```

Tests must be run with `gtimeout`.

## Code Quality

**IMPORTANT:** Run the following steps in sequence before every commit. All three must pass — do not skip or commit on failure. Stop and fix on any failure — never skip with `--no-verify`.

```bash
dotnet build
dotnet format
gtimeout 120 dotnet test
```

## Architecture

### SegmentedStringPool (primary)

Two-tier allocator: small strings go to the slab tier; larger strings go to the arena tier.

**Slab tier** (`SegmentedSlabTier`, `SegmentedSlab`)
- Five size classes: 8, 16, 32, 64, 128 chars
- Each class is an intrusive singly-linked chain of fixed-cell slabs backed by unmanaged memory
- Bitmap tracking (1 = free); `BitOperations.TrailingZeroCount` gives O(1) allocation
- Full slabs are unlinked from the active chain; freeing a cell re-links if previously full

**Arena tier** (`SegmentedArenaTier`, `SegmentedArenaSegment`)
- Bump allocator from the tail + segregated free-list bins (keyed by Log2(blockSize)) from the head
- Free blocks embed a `SegmentedFreeBlockHeader` inline in the freed memory
- Oversized allocations get a dedicated segment; existing segments are never resized or moved

**Slot table** (`SegmentedSlotTable`, `SegmentedSlotEntry`)
- Dynamically-growing array; doubles on growth
- Intrusive free-list via generation high bit + Ptr field; generation increments on reuse to detect dangling refs

**Handle** (`PooledStringRef`)
- 16-byte readonly struct; no heap allocation
- Validated via slot generation; invalidated on disposal or explicit free

### UnmanagedStringPool (legacy)
- Single contiguous unmanaged memory block; thread-safe reads, external sync for mutations
- Auto-grows with configurable factor; finalizer cleans up unmanaged memory
- `PooledString`: 12-byte struct (pool reference + allocation ID); allocation ID 0 reserved for empty strings

## Code Style

- Test namespace: `LookBusy.Test`
- Formatting and analyzer rules enforced by `.editorconfig` and the build
- Prefer pre-increment (`++a`) over post-increment; use post-increment (`a++`) only when the pre-increment value is required (e.g. `array[a++] = 1`)
- Property getters must be trivially cheap (field read or simple arithmetic). Any getter that loops, allocates, or does non-trivial work must be a method (`GetFoo()`) so callers see the cost.
