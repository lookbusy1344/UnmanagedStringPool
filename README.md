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

Two patterns measured against a managed string baseline, parameterised by N (1,000 / 10,000) and StringLength (8 / 256 chars). Full results in [docs/benchmarks.md](docs/benchmarks.md).

**Large strings (256 chars) — key numbers:**

| Scenario | N | Pooled vs managed (speed) | Gen0 reduction | Allocated reduction |
|---|---|---|---|---|
| Bulk allocate | 10,000 | **17% faster** | 7× fewer | 92% less |
| Bulk allocate | 1,000 | 1.2× slower | 17× fewer | 94% less |
| Interleaved alloc/free | 10,000 | 1.8× slower | 6× fewer | 84% less |

**Small strings (8 chars) — pooled is 3.5–7× slower with no allocation benefit. Avoid the pool for short strings.**

```bash
# Run benchmarks (~90 seconds)
dotnet run --configuration Release --project Benchmarks -- --filter "*"

# Bulk or interleaved only
dotnet run --configuration Release --project Benchmarks -- --filter "*BulkAllocate*"
dotnet run --configuration Release --project Benchmarks -- --filter "*Interleaved*"
```

## Is this worthwhile?

**It depends entirely on string size.**

For large strings (256+ chars) at high volume the case is clear: 84–94% less managed allocation, Gen0 collections cut 6–17×, and bulk allocation at N=10,000 is 17% *faster* than managed. The pool's contiguous unmanaged layout amortises the per-allocation overhead once string data dominates bookkeeping cost.

For small strings (8 chars) it is actively harmful. The pool's per-allocation bookkeeping — a dictionary entry plus free-list tracking — outweighs the string data itself, producing more GC pressure and 3.5–7× worse throughput.

The crossover is somewhere between 8 and 256 chars. Benchmark at 32 and 64 chars against your actual workload before committing.

### Further benchmarking

To find the size threshold for your workload, add intermediate `StringLength` params to the benchmark classes:

```csharp
[Params(8, 32, 64, 128, 256)]
public int StringLength { get; set; }
```

Other scenarios worth measuring:
- **Mixed sizes** — real workloads rarely have uniform string lengths; a benchmark mixing short and long strings would surface the average-case tradeoff
- **Concurrent access** — the pool requires external synchronisation for writes; measure lock contention overhead under concurrent load
- **Pool reuse** — the benchmarks use a pre-warmed pool; measure cold-start (first-use) cost if allocation bursts are infrequent

### Possible improvements

- **Replace `Dictionary<uint, AllocationInfo>` with a flat array** indexed by allocation ID. The dictionary is the dominant source of managed allocation overhead. A pre-allocated array (or segmented array to avoid one large allocation) would eliminate resizing entirely and reduce managed bookkeeping to near zero.
- **Reduce `PooledString` from 12 to 8 bytes** by encoding the pool reference as an index rather than a pointer. Smaller structs reduce cache pressure when storing large arrays of `PooledString`.
- **Size threshold guard** — add a configurable minimum string length and throw (or fall back to a managed string) below it, making the misuse case explicit rather than silently slow.
- **Write-side locking built in** — currently callers must synchronise writes externally. An opt-in `ThreadSafeUnmanagedStringPool` wrapper with a `ReaderWriterLockSlim` would make concurrent use safer and benchmark-able.
- **`ReadOnlySpan<char>` allocation path** — `Allocate` currently takes a `string`; accepting `ReadOnlySpan<char>` directly would avoid the managed string allocation at the call site in parsing scenarios.

## Requirements

- .NET 10.0 or later

## License

MIT
