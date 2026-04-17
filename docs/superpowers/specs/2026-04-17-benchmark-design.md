# Benchmark Design: UnmanagedStringPool vs Managed Strings

**Date:** 2026-04-17
**Goal:** Quantitatively validate that `UnmanagedStringPool` reduces GC pressure and improves throughput under bulk allocation/deallocation workloads compared to normal managed strings.

---

## Project Structure

New project: `Benchmarks/StringPoolBenchmarks.csproj`

- `<OutputType>Exe</OutputType>`, targeting `net10.0`
- References `BenchmarkDotNet` (latest stable)
- Compiles `../UnmanagedStringPool.cs` and `../PooledString.cs` directly (same pattern as `Tests/`)
- Entry point: `Program.cs` calling `BenchmarkRunner.Run<StringPoolBenchmarks>()`
- Meaningful results only in Release configuration

---

## Benchmark Class

Single class: `StringPoolBenchmarks`, decorated with `[MemoryDiagnoser]`.

### Parameters

| Param | Values | Purpose |
|---|---|---|
| `N` | 100, 1_000, 10_000 | Scale of allocation workload |
| `StringLength` | 8, 64, 256 | Short / medium / long strings |

### Benchmark Methods

| Method | Pattern | Baseline |
|---|---|---|
| `BulkAllocate_Managed` | Allocate N `string` objects, hold in array | Yes |
| `BulkAllocate_Pooled` | Allocate N `PooledString` from pool, hold in array | No |
| `InterleavedAllocFree_Managed` | Allocate 3 strings, free oldest, repeat N times | Yes |
| `InterleavedAllocFree_Pooled` | Same pattern with pool | No |

### Pool Sizing

Pool is pre-sized at `N * StringLength * sizeof(char) * 4` bytes before each benchmark run (via `[GlobalSetup]`). This avoids triggering pool growth during measurement — we benchmark allocation behaviour, not growth.

### String Content

Use a fixed deterministic string of the target length (e.g. `new string('x', StringLength)`) rather than random content. This keeps the benchmark reproducible and avoids noise from string generation.

---

## Expected Output

With `[MemoryDiagnoser]`, BenchmarkDotNet reports per-operation:

- **Mean time** (ns/μs)
- **Allocated bytes** (managed heap — unmanaged pool memory intentionally not counted)
- **Gen0 / Gen1 / Gen2** collection counts

The GC columns are the primary signal. Pooled strings should show near-zero Gen0 collections vs managed strings, since `PooledString` is a 12-byte struct on the stack with no managed heap allocation per string.

---

## Success Criteria

- `BulkAllocate_Pooled` shows materially fewer Gen0 collections than `BulkAllocate_Managed` at N=10_000
- `InterleavedAllocFree_Pooled` shows fewer or zero Gen0 collections vs managed equivalent
- Allocated bytes for pooled variants is negligible (struct overhead only, not per-string heap objects)
- Results are reproducible across runs (low StdDev relative to Mean)
