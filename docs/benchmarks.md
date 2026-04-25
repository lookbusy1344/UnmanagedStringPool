# Benchmark Results

Hardware: Apple M4 Pro, 14-core  
Runtime: .NET 10.0.7, Arm64 RyuJIT AdvSIMD  
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
| BulkAllocate_Managed | 1,000 | 8 | 4.60 µs | 1.00 | 5.74 | 0.77 | — | 46.9 KB | 1.00 |
| BulkAllocate_Legacy | 1,000 | 8 | 17.92 µs | 3.89 | 3.88 | 0.21 | — | 31.9 KB | 0.68 |
| BulkAllocate_Segmented | 1,000 | 8 | 11.51 µs | 2.50 | 1.91 | — | — | 15.7 KB | 0.33 |
| BulkAllocate_Managed | 1,000 | 256 | 21.69 µs | 1.00 | 65.03 | 24.02 | — | 531.3 KB | 1.00 |
| BulkAllocate_Legacy | 1,000 | 256 | 26.19 µs | 1.21 | 3.88 | 0.21 | — | 31.9 KB | **0.06** |
| BulkAllocate_Segmented | 1,000 | 256 | 30.15 µs | 1.39 | 1.89 | — | — | 15.7 KB | **0.03** |
| BulkAllocate_Managed | 10,000 | 8 | 51.20 µs | 1.00 | 57.13 | 28.50 | — | 468.8 KB | 1.00 |
| BulkAllocate_Legacy | 10,000 | 8 | 177.80 µs | 3.47 | 90.82 | 90.82 | 90.82 | 412.6 KB | 0.88 |
| BulkAllocate_Segmented | 10,000 | 8 | 120.49 µs | 2.35 | 49.93 | 49.93 | 49.93 | 156.3 KB | 0.33 |
| BulkAllocate_Managed | 10,000 | 256 | 312.11 µs | 1.00 | 649.90 | 324.71 | — | 5,312.5 KB | 1.00 |
| BulkAllocate_Legacy | 10,000 | 256 | 261.17 µs | **0.84** | 90.82 | 90.82 | 90.82 | 412.6 KB | **0.08** |
| BulkAllocate_Segmented | 10,000 | 256 | 332.99 µs | 1.07 | 49.80 | 49.80 | 49.80 | 156.3 KB | **0.03** |

---

## InterleavedAllocFree

Sliding window of 3 live strings: on each iteration, allocate into a slot and free the evicted string. Tests the pool's free-block coalescing and fragmentation handling under steady-state churn.

| Method | N | StringLength | Mean | Ratio | Gen0 | Gen1 | Allocated | Alloc Ratio |
|---|---|---|---|---|---|---|---|---|
| InterleavedAllocFree_Managed | 1,000 | 8 | 4.16 µs | 1.00 | 4.78 | — | 39.1 KB | 1.00 |
| InterleavedAllocFree_Legacy | 1,000 | 8 | 28.13 µs | 6.77 | 10.50 | — | 85.8 KB | 2.20 |
| InterleavedAllocFree_Segmented | 1,000 | 8 | 21.88 µs | 5.26 | — | — | — | **0.00** |
| InterleavedAllocFree_Managed | 1,000 | 256 | 20.11 µs | 1.00 | 64.06 | 0.37 | 523.4 KB | 1.00 |
| InterleavedAllocFree_Legacy | 1,000 | 256 | 36.62 µs | 1.82 | 10.50 | — | 85.8 KB | **0.16** |
| InterleavedAllocFree_Segmented | 1,000 | 256 | 25.20 µs | 1.25 | — | — | — | **0.00** |
| InterleavedAllocFree_Managed | 10,000 | 8 | 42.88 µs | 1.00 | 47.79 | — | 390.6 KB | 1.00 |
| InterleavedAllocFree_Legacy | 10,000 | 8 | 281.11 µs | 6.56 | 104.98 | — | 859.2 KB | 2.20 |
| InterleavedAllocFree_Segmented | 10,000 | 8 | 234.30 µs | **5.46** | — | — | — | **0.00** |
| InterleavedAllocFree_Managed | 10,000 | 256 | 201.93 µs | 1.00 | 640.63 | 0.24 | 5,234.4 KB | 1.00 |
| InterleavedAllocFree_Legacy | 10,000 | 256 | 341.50 µs | 1.69 | 104.98 | — | 859.2 KB | **0.16** |
| InterleavedAllocFree_Segmented | 10,000 | 256 | 270.54 µs | 1.34 | — | — | — | **0.00** |

> **Note on N=10,000 / 256 chars interleaved**: Segmented's throughput ratio at this point (1.34×) has improved and stabilised relative to prior runs. The zero-allocation result is stable. Legacy's ratio remains 1.69× at this N and string size.

---

## Analysis

### Legacy pool: large-string bulk remains the sweet spot

At 256 chars and N=10,000, Legacy is ~16% faster than managed (ratio 0.84) and allocates only 8% of managed heap bytes. The single contiguous block layout plus dictionary-tracked allocation IDs pays off when string data dominates bookkeeping.

For small strings (8 chars) or interleaved churn the picture reverses: the per-allocation dictionary entry and free-list node cost 3.5–6.8× the throughput of plain managed strings, with no meaningful reduction in managed allocations.

### Segmented pool: significant bulk improvements, small-string interleaved regression

**Bulk workload** results improved substantially versus the previous run:

- At N=10,000 / 256 chars, Segmented is now only 1.07× slower than managed (down from 1.20×) — nearly parity, while still producing 97% less managed allocation.
- At N=10,000 / 8 chars, Segmented improved from 4.02× to 2.35× — now clearly ahead of Legacy (3.47×) in both throughput and allocation.

The slot-table shrink on `ClearAllSlots` and the `ReserveSmall/ReserveLarge` pre-allocation spreading across size classes are the primary drivers.

**Interleaved churn** results are split by string size:

- 256-char strings improved: N=10,000 dropped from 1.59× to 1.34×, now beating Legacy (1.69×) in throughput *and* producing zero managed allocation.
- 8-char strings regressed: N=1,000 went from 2.03× to 5.26×; N=10,000 went from 2.04× to 5.46×. Zero managed allocation is preserved, but throughput is now comparable to Legacy's (6.56×) rather than the 2–3× advantage seen previously.

The 8-char regression traces to the slot-entry widening from 16 to 32 bytes (`fix(P0-2+P1-1)`, which added `Owner` and `ActualBytes` fields for correctness). For 8-char strings, the slab bitmap lookup is fast enough that slot-table access becomes the dominant cost; doubling slot-entry size degrades cache utilisation in the tight free→alloc cycle. The regression is a trade-off for the correctness guarantees the wider entry provides.

**Zero managed allocation** is preserved across all interleaved scenarios regardless of string size or N.

### When to use which

| Pattern | String size | Recommendation |
|---|---|---|
| Bulk allocate, high N | Large (256+) | **Segmented** — 1.07× parity with managed, 97% alloc reduction |
| Bulk allocate, high N | Small (8) | **Segmented** — 2.35× slower but 67% fewer managed allocations than Legacy |
| Bulk allocate, low N | Large (256+) | Either — Legacy 1.21×, Segmented 1.39×, both cut 94–97% alloc |
| Interleaved churn | Large (256+) | **Segmented** — zero managed allocation, 1.34× vs Legacy 1.69× |
| Interleaved churn | Small (8) | **Managed** if throughput dominates; **Segmented** if zero-GC is required |

### Why pooled allocated bytes are constant across N

The `Allocated` column reflects managed heap only. For a pre-warmed pool, the only managed allocations are the result array (`PooledString[]` or `PooledStringRef[]`, N × struct size) and per-allocation bookkeeping. Legacy's constant ~413 KB at N=10,000 is the result array (N × 12 bytes) plus dictionary overhead; Segmented's constant ~156 KB is the result array (N × 8 bytes, `PooledStringRef` is smaller) with no additional bookkeeping — the string data lives entirely in unmanaged memory.

In the interleaved pattern the result array is `WindowSize` elements (3), not N, which is why Segmented shows zero: its 3-element `PooledStringRef[]` is too small to register.
