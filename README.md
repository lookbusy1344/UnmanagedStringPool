# UnmanagedStringPool

[![Build and test](https://github.com/lookbusy1344/UnmanagedStringPool/actions/workflows/ci.yml/badge.svg)](https://github.com/lookbusy1344/UnmanagedStringPool/actions/workflows/ci.yml)

A high-performance .NET library for managing strings in unmanaged memory to reduce garbage collection pressure in string-intensive applications.

## Overview

`UnmanagedStringPool` allocates a single contiguous block of unmanaged memory and provides string storage as lightweight `PooledString` structs. This approach eliminates per-string heap allocations and significantly reduces GC overhead in scenarios with high string throughput or large string datasets.

## Key Features

- **Zero GC Pressure**: Strings stored entirely in unmanaged memory
- **Value Type Semantics**: `PooledString` is a 12-byte struct with full copy semantics
- **Automatic Memory Management**: Built-in defragmentation, growth, and coalescing
- **Thread-Safe Reads**: Multiple threads can read strings concurrently
- **Memory Efficient**: 8-byte alignment, free block coalescing, size-indexed allocation
- **Safe Design**: Allocation IDs prevent use-after-free bugs

## Design

### Why Unmanaged Memory?

Traditional .NET strings are immutable objects on the managed heap. In high-throughput scenarios (parsers, caches, data processing), this creates significant GC pressure:
- Each string allocation triggers potential GC
- Gen 0 collections become frequent
- Large strings promote to Gen 2, causing expensive full GCs
- Memory fragmentation from many small string objects

### Rationale

- **Finalizers** are needed to ensure unmanaged memory cleanup, but structs don't support them. We need a class.
- A class-per-string would create significant GC load (even if the strings were stored in unmanaged memory), so instead the
  finalizable class represents a 'pool', which can hold several strings and performs just one unmanaged memory allocation.
- Instances of individual pooled strings are **structs**, pointing into a pool object. They have full **copy semantics** and don't involve any
  heap allocation.

- The pool implements **IDisposable**, with a finalizer, for memory safety.
- Invalid pointers are never dereferenced. If the pool is disposed, any string structs relying on it automatically become invalid.
- The pool is deterministically freed, but the tiny pool object itself gets GC's normally (about 100 bytes)
- If the string within the pool is freed, the `allocation_id` is not reused so any string structs pointing to it become invalid. Reusing
  the memory in the pool will result in a different id, preventing old string structs pointing to the new string.
- Freed space in the pool is reused where possible, and periodically compacted

### Architecture details

1. **Single Memory Block**: One large allocation instead of thousands of small ones reduces OS memory management overhead

2. **Struct-Based References**: `PooledString` structs (12 bytes) are stack-allocated or embedded in other structs, eliminating heap allocations for references

3. **Allocation IDs**: Each allocation gets a unique, never-reused ID. This prevents dangling references - if a string is freed and reallocated, old `PooledString` instances become safely invalid

4. **Automatic Defragmentation**: At 35% fragmentation threshold, the pool automatically compacts memory, updating all internal references transparently

5. **Size-Indexed Free Lists**: Free blocks are tracked by size buckets for O(1) best-fit allocation

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

## Basic Usage

```csharp
// Create a pool with 1MB initial size
using var pool = new UnmanagedStringPool(1024 * 1024);

// Allocate strings
PooledString str1 = pool.AllocateString("Hello, World!");
PooledString str2 = pool.AllocateString("Unmanaged strings!");

// Create empty strings (optimized - no memory allocation)
PooledString empty = pool.CreateEmptyString();

// Use spans directly to avoid heap allocations
Console.Out.WriteLine(str1.AsSpan());  // Console.Out.WriteLine accepts ReadOnlySpan<char>
int length = str2.Length;
char firstChar = str2[0];

// Strings can be explicitly freed
str1.Dispose();

// Pool automatically cleans up remaining allocations on disposal
```

### Empty String Behavior

Empty strings receive special optimization:
- Use reserved allocation ID (0) with no actual memory allocation
- Remain valid for read operations even after other strings are freed
- **Important**: Become invalid after pool disposal since operations like `Insert()` require the pool to allocate memory for the resulting non-empty string
- All empty strings from any pool are considered equal

## Test Suite

The project includes comprehensive test coverage across multiple areas:

- **UnmanagedStringPoolTests.cs**: Core functionality and basic operations
- **UnmanagedStringPoolEdgeCaseTests.cs**: Edge cases and error conditions
- **FragmentationAndMemoryTests.cs**: Memory management and defragmentation
- **FragmentationTest.cs**: Specific fragmentation scenarios
- **PooledStringTests.cs**: String operations and manipulations
- **ConcurrentAccessTests.cs**: Thread safety and concurrent operations
- **DisposalAndLifecycleTests.cs**: Object disposal and lifecycle management
- **FinalizerBehaviorTests.cs**: Finalizer and GC interaction tests
- **ClearMethodTests.cs**: Pool clearing operations
- **IntegerOverflowTests.cs**: Overflow protection and boundary conditions

## Performance Characteristics

- **Allocation**: O(1) average case with size-indexed free lists
- **Deallocation**: O(1) with immediate coalescing
- **Defragmentation**: O(n) where n is active allocations, triggered automatically
- **Memory Overhead**: ~8 bytes per allocation for alignment and metadata
- **Growth**: Configurable growth factor (default 2x) when pool exhausted

## Thread Safety

- **Read Operations**: Fully thread-safe
- **Write Operations**: Require external synchronization
- **Disposal**: Not thread-safe, ensure exclusive access

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
| Bulk allocate | 10,000 | Legacy | **~19% faster** | 2.9× | 92% |
| Bulk allocate | 10,000 | Segmented | 1.2× slower | **4.3×** | **97%** |
| Bulk allocate | 1,000 | Legacy | 1.2× slower | 17× | 94% |
| Bulk allocate | 1,000 | Segmented | 1.4× slower | **34×** | **97%** |
| Interleaved alloc/free | 10,000 | Legacy | 1.7× slower | 6.1× | 84% |
| Interleaved alloc/free | 10,000 | Segmented | **1.3–1.6× slower** | **zero Gen0** | **100%** |

**Small strings (8 chars):**

| Scenario | N | Pool | Speed vs managed | Alloc vs managed |
|---|---|---|---|---|
| Bulk allocate | 10,000 | Legacy | 3.6× slower | 88% |
| Bulk allocate | 10,000 | Segmented | 4.0× slower | **33%** |
| Interleaved alloc/free | 10,000 | Legacy | 6.7× slower | 220% (worse!) |
| Interleaved alloc/free | 10,000 | Segmented | **2.0× slower** | **0% (zero alloc)** |

```bash
# Run benchmarks (~90 seconds)
dotnet run --configuration Release --project Benchmarks -- --filter "*"

# Bulk or interleaved only
dotnet run --configuration Release --project Benchmarks -- --filter "*BulkAllocate*"
dotnet run --configuration Release --project Benchmarks -- --filter "*Interleaved*"
```

## Which should I use?

### Use plain managed strings when:

- Strings are short (≤ ~32 chars) and you are not dominated by GC pauses. Managed is 2–4× faster than either pool at 8 chars, with no added complexity.
- Allocation rate is low. Pool overhead only pays off under sustained high-throughput pressure.
- You need string interning, `Dictionary` keys, or any API that expects a real `string`. Both pools require converting back to a managed string at consumption points, erasing the savings.

### Use the Legacy pool (`UnmanagedStringPool`) when:

- Strings are large (256+ chars) **and** the dominant pattern is bulk allocate-then-free (not continuous churn). This is the only scenario where a pool is both faster and cheaper than managed: ~19% faster throughput at N=10,000 with 92% less managed allocation.
- Raw throughput is the priority and GC pauses are already acceptable. Legacy's contiguous block layout has the lowest per-access overhead when string data dominates bookkeeping.

### Use the Segmented pool (`SegmentedStringPool`) when:

- The workload involves **continuous churn** — strings are allocated and freed throughout the operation rather than in two distinct phases. Segmented's slab/arena tiers recycle cells without any managed allocation, producing zero Gen0 collections in the interleaved benchmark regardless of string size or N.
- String sizes are **mixed or unpredictable**. Legacy is catastrophic for small strings under churn (6.7× slower, creates *more* managed allocation than baseline); Segmented is only 2× slower and still allocates nothing.
- You cannot easily characterise your workload. Segmented's worst case (~1.4–1.6× slower than managed) is far less damaging than Legacy's worst case (small-string churn, 6.7× slower with 2.2× more managed allocation).

**Important caveat — Segmented is never faster than managed strings in isolation.** The best case is 1.15× slower (large strings, interleaved, N=1,000). The benefit is GC pause elimination: at N=10,000 interleaved, managed strings trigger ~640 Gen0 collections per iteration; Segmented triggers zero. Gen0 is fast but not free — under concurrent load those stop-the-world pauses compound. In a latency-sensitive application (request handling, game loop, real-time processing) where string allocation is a meaningful fraction of the work, removing those pauses can improve end-to-end throughput even though individual `Allocate` calls are slower. If GC pauses are not visible in your latency profile, plain managed strings are the right choice.

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
