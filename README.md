# UnmanagedStringPool

[![Build and test](https://github.com/lookbusy1344/UnmanagedStringPool/actions/workflows/ci.yml/badge.svg)](https://github.com/lookbusy1344/UnmanagedStringPool/actions/workflows/ci.yml)

A high-performance .NET 10 library for storing strings in unmanaged memory to eliminate GC pressure in string-intensive workloads.

## Overview

Two implementations are provided:

- **`SegmentedStringPool`** — the current implementation. A two-tier allocator (slab + arena) that produces zero managed allocation per string operation in steady state. Handles are 16-byte `PooledStringRef` structs.
- **`UnmanagedStringPool`** — the legacy implementation. A single contiguous block with a managed dictionary for metadata. Still the fastest option for bulk large-string workloads, but generates more GC pressure under churn. Handles are 12-byte `PooledString` structs.

See [Which should I use?](#which-should-i-use) for a benchmark-backed decision guide.

## Key Features

**Both pools:**
- Strings stored entirely in unmanaged memory — no per-string heap allocation
- Value-type handles with full copy semantics; no heap allocation for references
- Thread-safe reads; writes require external synchronisation
- Generation counters on handles prevent use-after-free

**Segmented pool additionally:**
- Zero managed allocation per `Allocate`/`Free` in steady state
- Slab tier: O(1) alloc/free for strings ≤128 chars via bitmap + `BitOperations.TrailingZeroCount`
- Arena tier: bump-allocated segments; free-block headers live inside freed unmanaged memory
- No in-place defragmentation — segments grow by appending, never by copying

## Design

### Why Unmanaged Memory?

Traditional .NET strings are immutable objects on the managed heap. In high-throughput scenarios (parsers, caches, data processing), this creates significant GC pressure:
- Each string allocation triggers potential GC
- Gen 0 collections become frequent
- Large strings promote to Gen 2, causing expensive full GCs
- Memory fragmentation from many small string objects

The pool approach makes one (or a few) large unmanaged allocations and hands out references into them — one finalizable class object per pool, not per string.

### Segmented pool architecture

`SegmentedStringPool` is a sibling implementation that replaces the legacy pool's single contiguous block + dictionary metadata with a tiered allocator. It targets workloads where managed GC pressure from per-allocation bookkeeping (rather than from the string data itself) is the dominant cost.

1. **Slot table** — managed `SlotEntry[]` indexed by handle; the only managed array that scales with live-string count. A generation counter on each slot, with the high bit doubling as a freed flag, prevents use-after-free without exhausting allocation IDs.
2. **Slab tier** — strings ≤128 chars route to fixed-size-class slabs (8/16/32/64/128 chars). Each slab tracks cell occupancy via a bitmap (`1 = free`) so `BitOperations.TrailingZeroCount` returns the next free cell in a single x86 instruction. Slabs in each size class are threaded into an intrusive linked list via `NextInClass`; allocation and free are O(1).
3. **Arena tier** — strings >128 chars go into bump-allocated 1 MB segments with coalesced free-block bins. Free-block headers (`size`, `next`, `prev`, `bin`) live **inside** the freed unmanaged memory itself — no managed allocation for the free list at all.

Steady-state result: zero managed allocation per string, no in-place defragmentation (segments grow by appending, not by copying), and a 16-byte `PooledStringRef` handle (pool reference + slot index + generation) instead of `PooledString`'s 12-byte allocation-id reference.

For pointer tagging, slab bitmap mechanics, intrusive linked-list construction, and the slot generation field, see **[docs/segmented-pool-internals.md](docs/segmented-pool-internals.md)**.

## Use Cases

Ideal for:
- High-frequency string parsing and processing
- Large in-memory caches with string keys/values
- Protocol buffers and message processing
- Game engines with extensive text/localization
- Any scenario where string GC becomes a bottleneck

Not recommended for:
- Long-lived strings that rarely change
- Scenarios requiring string interning
- Applications with low string allocation rates
- Any consumption point that calls `.ToString()` — converting a pooled string to a managed `string` allocates on the heap and erases most of the GC benefit. Favour `ReadOnlySpan<char>` via `.AsSpan()` throughout the hot path.

## Basic Usage

### SegmentedStringPool (recommended)

```csharp
using var pool = new SegmentedStringPool();

PooledStringRef str1 = pool.Allocate("Hello, World!");
PooledStringRef str2 = pool.Allocate("Segmented strings!");

// Always use AsSpan() — calling ToString() allocates a managed string and loses the GC benefit
Console.Out.WriteLine(str1.AsSpan());
int length = str2.Length;
char firstChar = str2[0];

str1.Dispose(); // returns memory to the slab/arena for reuse
```

> **Important:** keep `ReadOnlySpan<char>` throughout the hot path. The moment you call `.ToString()` you allocate a managed string and discard most of the benefit. Any API that accepts `ReadOnlySpan<char>` (e.g. `Console.Out.WriteLine`, `MemoryExtensions` helpers, parsers) avoids this.

### UnmanagedStringPool (legacy)

```csharp
// Create a pool with 1MB initial size
using var pool = new UnmanagedStringPool(1024 * 1024);

PooledString str1 = pool.AllocateString("Hello, World!");

Console.Out.WriteLine(str1.AsSpan());

str1.Dispose();
```

## Test Suite

**Segmented pool:**
- `SegmentedStringPoolTests.cs` — core API and allocation behaviour
- `SegmentedStringPoolLifecycleTests.cs` — disposal and invalidation
- `SegmentedSlabTests.cs` / `SegmentedSlabTierTests.cs` — slab tier and size-class routing
- `SegmentedArenaSegmentTests.cs` / `SegmentedArenaTierTests.cs` — arena bump/free-list logic
- `SegmentedSlotTableTests.cs` — slot table growth and generation tracking
- `PooledStringRefTests.cs` — handle semantics and copy behaviour
- `GcPressureTests.cs` — verifies zero managed allocation in steady state

**Legacy pool:**
- `UnmanagedStringPoolTests.cs` / `UnmanagedStringPoolEdgeCaseTests.cs` — core and edge cases
- `FragmentationAndMemoryTests.cs` / `FragmentationTest.cs` — defrag and coalescing
- `PooledStringTests.cs` — string operations on `PooledString`
- `ConcurrentAccessTests.cs` — thread-safety under concurrent reads
- `DisposalAndLifecycleTests.cs` / `FinalizerBehaviorTests.cs` — lifecycle and finalizer
- `ClearMethodTests.cs` / `IntegerOverflowTests.cs` — reset and overflow guards
- `CopyBehaviorTests.cs` — struct copy semantics

## Performance Characteristics

**SegmentedStringPool:**
- Allocation (slab tier, ≤128 chars): O(1) — bitmap scan via `TrailingZeroCount`
- Allocation (arena tier, >128 chars): O(1) amortised — free-list bin lookup then bump fallback
- Deallocation: O(1) with immediate coalescing in arena tier
- No defragmentation pass — segments grow by appending; freed slab cells recycle in place
- Managed allocation per operation: zero in steady state

**UnmanagedStringPool (legacy):**
- Allocation: O(1) average with size-indexed free lists
- Deallocation: O(1) with immediate coalescing
- Defragmentation: O(n) triggered automatically at 35% fragmentation threshold
- Memory overhead: ~8 bytes per allocation for alignment and metadata

## Thread Safety

Both pools: reads are fully thread-safe; writes require external synchronisation; disposal is not thread-safe.

## Building and Testing

```bash
# Build
dotnet build

# Run tests
dotnet test

# Run with detailed output
dotnet test --logger:"console;verbosity=detailed"

# Run specific test class
dotnet test --filter "FullyQualifiedName~UnmanagedStringPoolTests"

# Format code (important after making changes)
dotnet format
```

For detailed information about the test suite and coverage areas, see [Tests/README.md](Tests/README.md).

## Benchmarks

Two patterns measured against a managed string baseline, parameterised by N (1,000 / 10,000) and StringLength (8 / 256 chars). Three implementations compared: **Managed** (baseline), **Legacy** (`UnmanagedStringPool`), **Segmented** (`SegmentedStringPool`). Full results in [docs/benchmarks.md](docs/benchmarks.md).

**Large strings (256 chars) — key numbers:**

| Scenario | N | Pool | Speed vs managed | Gen0 reduction | Alloc reduction |
|---|---|---|---|---|---|
| Bulk allocate | 10,000 | Legacy | **~16% faster** | 7.2× | 92% |
| Bulk allocate | 10,000 | Segmented | **~parity (1.07×)** | **13×** | **97%** |
| Bulk allocate | 1,000 | Legacy | 1.2× slower | 17× | 94% |
| Bulk allocate | 1,000 | Segmented | 1.4× slower | **34×** | **97%** |
| Interleaved alloc/free | 10,000 | Legacy | 1.7× slower | 6.1× | 84% |
| Interleaved alloc/free | 10,000 | Segmented | **1.3× slower** | **zero Gen0** | **100%** |

**Small strings (8 chars):**

| Scenario | N | Pool | Speed vs managed | Alloc vs managed |
|---|---|---|---|---|
| Bulk allocate | 10,000 | Legacy | 3.5× slower | 88% |
| Bulk allocate | 10,000 | Segmented | **2.4× slower** | **33%** |
| Interleaved alloc/free | 10,000 | Legacy | 6.6× slower | 220% (worse!) |
| Interleaved alloc/free | 10,000 | Segmented | 5.5× slower | **0% (zero alloc)** |

```bash
# Run benchmarks (~90 seconds)
dotnet run --configuration Release --project Benchmarks -- --filter "*"

# Bulk or interleaved only
dotnet run --configuration Release --project Benchmarks -- --filter "*BulkAllocate*"
dotnet run --configuration Release --project Benchmarks -- --filter "*Interleaved*"
```

## Why Segmented is generally superior

The legacy pool's fundamental problem is metadata cost. Every allocated string requires an entry in a managed `Dictionary<uint, ...>` — one object on the managed heap per live string. Under continuous churn (strings allocated and freed throughout an operation), those dictionary entries generate steady Gen0 pressure regardless of where the string data lives. At N=10,000 interleaved, the benchmarks record ~640 Gen0 collections per iteration even though all string bytes are in unmanaged memory.

`SegmentedStringPool` eliminates this by moving all per-string metadata into unmanaged memory or into a plain `SlotEntry[]` array that never grows under normal churn (slots are recycled via an intrusive free list). The result is zero managed allocation per `Allocate`/`Free` in steady state.

The architectural differences that make this possible:

| | Legacy | Segmented |
|---|---|---|
| String data | Single contiguous block | Slab cells / arena segments |
| Per-string metadata | Managed `Dictionary` entry | Slot in `SlotEntry[]` (recycled) |
| Free-list headers | Managed objects | Embedded in freed unmanaged memory |
| Small string strategy | Same allocator as large strings | Dedicated bitmap slabs per size class |
| Defragmentation | O(n) compaction pass at 35% threshold | Not needed — slabs recycle in place |
| Handle size | 12 bytes (`PooledString`) | 16 bytes (`PooledStringRef`) |

**Where the legacy pool still wins:** bulk allocate-then-free of large strings (≥256 chars) where the working set is stable, not churning. At N=10,000 bulk, legacy is ~16% faster than managed; Segmented is at near-parity (1.07×) while producing 97% less managed allocation. The gap has narrowed but Legacy still has the raw throughput edge.

**The general recommendation:** use `SegmentedStringPool` unless benchmarks on your specific workload show Legacy is faster. For large-string bulk Legacy retains a throughput advantage; for everything else — especially mixed sizes and churn — Segmented's zero-allocation property eliminates Gen0 pressure that compounds under concurrent load. Legacy's worst case (small-string churn: 6.6× slower, 2.2× *more* managed allocation than baseline) is significantly worse than its cost in isolation.

## Which should I use?

### Use plain managed strings when:

- Strings are short (≤ ~32 chars) and you are not dominated by GC pauses. Managed is 2–4× faster than either pool at 8 chars, with no added complexity.
- Allocation rate is low. Pool overhead only pays off under sustained high-throughput pressure.
- You need string interning, `Dictionary` keys, or any API that only accepts a `string`. Calling `.ToString()` on a pooled string allocates a managed string on the heap — if your consumption points can't accept `ReadOnlySpan<char>`, the pool savings are largely erased.

### Use the Legacy pool (`UnmanagedStringPool`) when:

- Strings are large (256+ chars) **and** the dominant pattern is bulk allocate-then-free (not continuous churn). This is the only scenario where a pool is both faster and cheaper than managed: ~19% faster throughput at N=10,000 with 92% less managed allocation.
- Raw throughput is the priority and GC pauses are already acceptable. Legacy's contiguous block layout has the lowest per-access overhead when string data dominates bookkeeping.

### Use the Segmented pool (`SegmentedStringPool`) when:

- The workload involves **continuous churn** — strings are allocated and freed throughout the operation rather than in two distinct phases. Segmented's slab/arena tiers recycle cells without any managed allocation, producing zero Gen0 collections in the interleaved benchmark regardless of string size or N.
- String sizes are **mixed or unpredictable**. Legacy is catastrophic for small strings under churn (6.7× slower, creates *more* managed allocation than baseline); Segmented is only 2× slower and still allocates nothing.
- You cannot easily characterise your workload. Segmented's worst case (~1.4–1.6× slower than managed) is far less damaging than Legacy's worst case (small-string churn, 6.7× slower with 2.2× more managed allocation).

**Important caveat — Segmented is never faster than managed strings in isolation.** The best case is 1.07× slower (large strings, bulk, N=10,000). The benefit is GC pause elimination: at N=10,000 interleaved, managed strings trigger ~640 Gen0 collections per iteration; Segmented triggers zero. Gen0 is fast but not free — under concurrent load those stop-the-world pauses compound. In a latency-sensitive application (request handling, game loop, real-time processing) where string allocation is a meaningful fraction of the work, removing those pauses can improve end-to-end throughput even though individual `Allocate` calls are slower. If GC pauses are not visible in your latency profile, plain managed strings are the right choice. Note also that for small-string (8-char) interleaved churn, Segmented has regressed to ~5.5× slower than managed (while still producing zero managed allocation); at that point managed strings are the throughput-optimal choice unless eliminating Gen0 is the explicit goal.

### Summary

| | Managed strings | Legacy pool | Segmented pool |
|---|---|---|---|
| Small strings, any pattern | **best** | avoid | acceptable |
| Large strings, bulk | good | **best** | good |
| Large strings, churn | poor | poor | **best** |
| Small strings, churn | **best** on speed | **avoid** | best on GC |
| Needs `string` API | **only option** | — | — |

The crossover between Legacy and Segmented for bulk workloads is somewhere between 8 and 256 chars. If bulk throughput is critical, benchmark at 32 and 64 chars with your actual string sizes before committing.

### Further benchmarking

To find the size threshold for your workload, add intermediate `StringLength` params to the benchmark classes:

```csharp
[Params(8, 32, 64, 128, 256)]
public int StringLength { get; set; }
```

Other scenarios worth measuring:
- **Concurrent access** — the pool requires external synchronisation for writes; measure lock contention overhead under concurrent load
- **Pool reuse** — the benchmarks use a pre-warmed pool; measure cold-start (first-use) cost if allocation bursts are infrequent

### Possible improvements

- **Size threshold guard** — add a configurable minimum string length and throw (or fall back to a managed string) below it, making the misuse case explicit rather than silently slow.
- **Write-side locking built in** — currently callers must synchronise writes externally. An opt-in thread-safe wrapper with a `ReaderWriterLockSlim` would make concurrent use safer and benchmark-able.
- **`ReadOnlySpan<char>` allocation path** — `Allocate` currently takes a `string`; accepting `ReadOnlySpan<char>` directly would avoid the managed string allocation at the call site in parsing scenarios.
- **Mixed-size benchmark** — real workloads rarely have uniform string lengths; a benchmark mixing short and long strings would surface the average-case tradeoff between the two pools.

## Requirements

- .NET 10.0 or later

## License

MIT
