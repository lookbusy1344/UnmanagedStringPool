# Benchmark Results

Hardware: Apple M4 Pro, 14-core  
Runtime: .NET 10.0.6, Arm64 RyuJIT AdvSIMD  
Configuration: `[SimpleJob(launchCount: 1, warmupCount: 1, iterationCount: 3)]`, `[MemoryDiagnoser]`

Run with:
```bash
dotnet run --configuration Release --project Benchmarks -- --filter "*"
```

---

## BulkAllocate

Allocates N strings then frees them all. Tests throughput and GC pressure for batch allocation workloads.

| Method | N | StringLength | Mean | Ratio | Gen0 | Gen1 | Gen2 | Allocated | Alloc Ratio |
|---|---|---|---|---|---|---|---|---|---|
| BulkAllocate_Managed | 1,000 | 8 | 4.57 µs | 1.00 | 5.74 | 0.77 | — | 46.9 KB | 1.00 |
| BulkAllocate_Pooled | 1,000 | 8 | 17.89 µs | 3.92 | 3.88 | 0.21 | — | 31.9 KB | 0.68 |
| BulkAllocate_Managed | 1,000 | 256 | 21.67 µs | 1.00 | 65.03 | 24.02 | — | 531.3 KB | 1.00 |
| BulkAllocate_Pooled | 1,000 | 256 | 25.93 µs | 1.20 | 3.88 | 0.21 | — | 31.9 KB | **0.06** |
| BulkAllocate_Managed | 10,000 | 8 | 50.52 µs | 1.00 | 57.13 | 28.50 | — | 468.8 KB | 1.00 |
| BulkAllocate_Pooled | 10,000 | 8 | 178.24 µs | 3.53 | 90.82 | 90.82 | 90.82 | 412.6 KB | 0.88 |
| BulkAllocate_Managed | 10,000 | 256 | 312.74 µs | 1.00 | 649.90 | 324.71 | — | 5,312.5 KB | 1.00 |
| BulkAllocate_Pooled | 10,000 | 256 | 259.41 µs | **0.83** | 90.82 | 90.82 | 90.82 | 412.6 KB | **0.08** |

---

## InterleavedAllocFree

Sliding window of 3 live strings: on each iteration, allocate into a slot and free the evicted string. Tests the pool's free-block coalescing and fragmentation handling under steady-state churn.

| Method | N | StringLength | Mean | Ratio | Gen0 | Gen1 | Allocated | Alloc Ratio |
|---|---|---|---|---|---|---|---|---|
| InterleavedAllocFree_Managed | 1,000 | 8 | 4.19 µs | 1.00 | 4.78 | — | 39.1 KB | 1.00 |
| InterleavedAllocFree_Pooled | 1,000 | 8 | 27.58 µs | 6.59 | 10.50 | — | 85.8 KB | 2.20 |
| InterleavedAllocFree_Managed | 1,000 | 256 | 20.14 µs | 1.00 | 64.06 | 0.37 | 523.4 KB | 1.00 |
| InterleavedAllocFree_Pooled | 1,000 | 256 | 35.54 µs | 1.76 | 10.50 | — | 85.8 KB | **0.16** |
| InterleavedAllocFree_Managed | 10,000 | 8 | 41.05 µs | 1.00 | 47.79 | — | 390.6 KB | 1.00 |
| InterleavedAllocFree_Pooled | 10,000 | 8 | 281.50 µs | 6.86 | 104.98 | — | 859.2 KB | 2.20 |
| InterleavedAllocFree_Managed | 10,000 | 256 | 199.41 µs | 1.00 | 640.63 | 3.66 | 5,234.4 KB | 1.00 |
| InterleavedAllocFree_Pooled | 10,000 | 256 | 362.75 µs | 1.82 | 104.98 | — | 859.2 KB | **0.16** |

---

## Analysis

### Large strings are the sweet spot

At 256 chars, pooled allocates 6–16% of managed heap memory with Gen0 collections 6–7× lower at N=10,000. The bulk pattern at N=10,000 is also 17% *faster* than managed — the pool's contiguous layout amortises the allocation overhead.

### Small strings are a net loss

At 8 chars, the pool's per-allocation bookkeeping (dictionary entry, free-list tracking) exceeds the string data itself. Pooled is 3.5–7× slower with no meaningful reduction in managed allocations. Avoid the pool for short strings.

### Throughput vs GC pressure trade-off

The interleaved pattern never beats managed on raw throughput, but cuts managed allocations by 84% at 256 chars. In GC-sensitive applications the throughput cost (1.8×) is often acceptable in exchange for eliminating hundreds of Gen0 collections per thousand operations.

### Why pooled allocated bytes are constant across N

The `Allocated` column reflects managed heap only. For a pre-warmed pool, the only managed allocation is the `PooledString[]` result array (N × 12 bytes per struct), which is why pooled allocated bytes scale with N independently of StringLength — the string data lives entirely in unmanaged memory.
