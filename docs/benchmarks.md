# Benchmark Results

Hardware: Apple M4 Pro, 14-core  
Runtime: .NET 10.0.6, Arm64 RyuJIT AdvSIMD  
Configuration: `[SimpleJob(launchCount: 1, warmupCount: 1, iterationCount: 3)]`, `[MemoryDiagnoser]`

Run with:
```bash
dotnet run --configuration Release --project Benchmarks -- --filter "*"
```

Three implementations compared: **Managed** (baseline .NET strings), **Legacy** (`UnmanagedStringPool` — single contiguous block with free-list), **Segmented** (`SegmentedStringPool` — slab/arena tiers).

---

## BulkAllocate

Allocates N strings then frees them all. Tests throughput and GC pressure for batch allocation workloads.

| Method | N | StringLength | Mean | Ratio | Gen0 | Gen1 | Gen2 | Allocated | Alloc Ratio |
|---|---|---|---|---|---|---|---|---|---|
| BulkAllocate_Managed | 1,000 | 8 | 4.49 µs | 1.00 | 5.74 | 0.77 | — | 46.9 KB | 1.00 |
| BulkAllocate_Legacy | 1,000 | 8 | 17.66 µs | 3.93 | 3.88 | 0.21 | — | 31.9 KB | 0.68 |
| BulkAllocate_Segmented | 1,000 | 8 | 9.25 µs | 2.06 | 1.91 | — | — | 15.7 KB | 0.33 |
| BulkAllocate_Managed | 1,000 | 256 | 21.25 µs | 1.00 | 65.03 | 24.02 | — | 531.3 KB | 1.00 |
| BulkAllocate_Legacy | 1,000 | 256 | 25.96 µs | 1.22 | 3.88 | 0.21 | — | 31.9 KB | **0.06** |
| BulkAllocate_Segmented | 1,000 | 256 | 30.36 µs | 1.43 | 1.89 | — | — | 15.7 KB | **0.03** |
| BulkAllocate_Managed | 10,000 | 8 | 50.27 µs | 1.00 | 57.13 | 28.50 | — | 468.8 KB | 1.00 |
| BulkAllocate_Legacy | 10,000 | 8 | 182.61 µs | 3.63 | 222.41 | 222.41 | 83.25 | 412.6 KB | 0.88 |
| BulkAllocate_Segmented | 10,000 | 8 | 200.19 µs | 3.98 | 152.10 | 152.10 | 43.46 | 156.3 KB | 0.33 |
| BulkAllocate_Managed | 10,000 | 256 | 319.36 µs | 1.00 | 649.90 | 324.71 | — | 5,312.5 KB | 1.00 |
| BulkAllocate_Legacy | 10,000 | 256 | 262.74 µs | **0.82** | 222.17 | 222.17 | 83.01 | 412.6 KB | **0.08** |
| BulkAllocate_Segmented | 10,000 | 256 | 388.85 µs | 1.22 | 152.34 | 152.34 | 42.97 | 156.3 KB | **0.03** |

---

## InterleavedAllocFree

Sliding window of 3 live strings: on each iteration, allocate into a slot and free the evicted string. Tests the pool's free-block coalescing and fragmentation handling under steady-state churn.

| Method | N | StringLength | Mean | Ratio | Gen0 | Gen1 | Allocated | Alloc Ratio |
|---|---|---|---|---|---|---|---|---|
| InterleavedAllocFree_Managed | 1,000 | 8 | 4.22 µs | 1.00 | 4.78 | — | 39.1 KB | 1.00 |
| InterleavedAllocFree_Legacy | 1,000 | 8 | 28.18 µs | 6.68 | 10.50 | — | 85.8 KB | 2.20 |
| InterleavedAllocFree_Segmented | 1,000 | 8 | 8.35 µs | 1.98 | — | — | — | **0.00** |
| InterleavedAllocFree_Managed | 1,000 | 256 | 19.90 µs | 1.00 | 64.06 | 0.37 | 523.4 KB | 1.00 |
| InterleavedAllocFree_Legacy | 1,000 | 256 | 35.63 µs | 1.79 | 10.50 | — | 85.8 KB | **0.16** |
| InterleavedAllocFree_Segmented | 1,000 | 256 | 22.21 µs | 1.12 | — | — | — | **0.00** |
| InterleavedAllocFree_Managed | 10,000 | 8 | 41.98 µs | 1.00 | 47.79 | — | 390.6 KB | 1.00 |
| InterleavedAllocFree_Legacy | 10,000 | 8 | 281.34 µs | 6.70 | 104.98 | — | 859.2 KB | 2.20 |
| InterleavedAllocFree_Segmented | 10,000 | 8 | 82.50 µs | **1.97** | — | — | — | **0.00** |
| InterleavedAllocFree_Managed | 10,000 | 256 | 197.86 µs | 1.00 | 640.63 | 0.24 | 5,234.4 KB | 1.00 |
| InterleavedAllocFree_Legacy | 10,000 | 256 | 344.27 µs | 1.74 | 104.98 | — | 859.2 KB | **0.16** |
| InterleavedAllocFree_Segmented | 10,000 | 256 | 254.37 µs | **1.29** | — | — | — | **0.00** |

---

## Analysis

### Legacy pool: large-string bulk is the sweet spot

At 256 chars and N=10,000, Legacy is 18% *faster* than managed and allocates only 8% of managed heap bytes. The single contiguous block layout plus dictionary-tracked allocation IDs pays off when string data dominates bookkeeping.

For small strings (8 chars) or interleaved churn the picture reverses: the per-allocation dictionary entry and free-list node cost 3.6–6.7× the throughput of plain managed strings, with no meaningful reduction in managed allocations.

### Segmented pool: zero managed allocation in churn scenarios

Segmented's defining result is the interleaved pattern: **zero managed bytes allocated** across all N and StringLength combinations. The slab/arena tiers recycle cells without touching the managed heap, so no dictionary entries or free-list nodes are created during steady-state churn. Gen0 collections are also eliminated entirely.

This makes Segmented viable for small-string churn — the pattern where Legacy was actively harmful. At N=10,000 interleaved with 8-char strings, Legacy is 6.7× slower than managed and allocates *more* managed memory; Segmented is only 2× slower and allocates nothing.

For large-string bulk, Segmented allocates 3% of managed bytes (vs Legacy's 8%), but the raw throughput trail: Legacy wins at N=10,000 (0.82× vs 1.22×). Legacy's contiguous block with a single large unmanaged allocation has lower per-access overhead than Segmented's slab lookup.

### When to use which

| Pattern | String size | Recommendation |
|---|---|---|
| Bulk allocate, high N | Large (256+) | **Legacy** — 18% faster, still cuts 92% of managed alloc |
| Bulk allocate, low N | Large (256+) | Either — Legacy 1.2×, Segmented 1.4×, both cut 94–97% alloc |
| Interleaved churn | Any size | **Segmented** — zero managed allocation, 2–3× faster than Legacy |
| Bulk or interleaved | Small (8 chars) | **Segmented** if GC matters; otherwise managed — both pools are slower |

### Why pooled allocated bytes are constant across N

The `Allocated` column reflects managed heap only. For a pre-warmed pool, the only managed allocations are the result array (`PooledString[]` or `PooledStringRef[]`, N × struct size) and per-allocation bookkeeping. Legacy's constant ~413 KB at N=10,000 is the result array (N × 12 bytes) plus dictionary overhead; Segmented's constant ~156 KB is the result array (N × 8 bytes, `PooledStringRef` is smaller) with no additional bookkeeping — the string data lives entirely in unmanaged memory.

In the interleaved pattern the result array is `WindowSize` elements (3), not N, which is why Segmented shows zero: its 3-element `PooledStringRef[]` is too small to register.
