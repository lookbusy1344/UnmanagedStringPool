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

// Use spans directly to avoid heap allocations
Console.Out.WriteLine(str1.AsSpan());  // Console.Out.WriteLine accepts ReadOnlySpan<char>
int length = str2.Length;
char firstChar = str2[0];

// Strings can be explicitly freed
str1.Dispose();

// Pool automatically cleans up remaining allocations on disposal
```

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
```

For detailed information about the test suite and coverage areas, see [Tests/README.md](Tests/README.md).

## Requirements

- .NET 9.0 or later

## License

MIT
