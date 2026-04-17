# Benchmark Project Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create a standalone BenchmarkDotNet project that measures allocation throughput and GC pressure for `UnmanagedStringPool` vs managed strings under bulk and interleaved allocation/free patterns.

**Architecture:** A new `Benchmarks/` console project compiles `UnmanagedStringPool.cs` and `PooledString.cs` directly (matching the Tests project pattern). Two benchmark classes — one per allocation pattern — each with a managed baseline method and a pooled comparison method, parameterised by N and StringLength. `[MemoryDiagnoser]` surfaces GC collection counts and allocated bytes per operation.

**Tech Stack:** .NET 10, BenchmarkDotNet 0.14.0, C# 13

---

## File Map

| File | Action | Responsibility |
|---|---|---|
| `Benchmarks/StringPoolBenchmarks.csproj` | Create | Project definition, BenchmarkDotNet reference, source file includes |
| `Benchmarks/Program.cs` | Create | Entry point; runs all benchmark classes via `BenchmarkSwitcher` |
| `Benchmarks/BulkAllocateBenchmarks.cs` | Create | Bulk-allocate-then-free pattern, managed baseline vs pooled |
| `Benchmarks/InterleavedAllocFreeBenchmarks.cs` | Create | Sliding-window allocate/free pattern, managed baseline vs pooled |

---

## Task 1: Create the project file

**Files:**
- Create: `Benchmarks/StringPoolBenchmarks.csproj`

- [ ] **Step 1: Create the project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Optimize>true</Optimize>
  </PropertyGroup>

  <PropertyGroup>
    <EnforceCodeStyleInBuild>True</EnforceCodeStyleInBuild>
    <AnalysisModeDesign>All</AnalysisModeDesign>
    <AnalysisModeSecurity>All</AnalysisModeSecurity>
    <AnalysisModePerformance>All</AnalysisModePerformance>
    <AnalysisModeReliability>All</AnalysisModeReliability>
    <AnalysisModeUsage>All</AnalysisModeUsage>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="BenchmarkDotNet" Version="0.14.0" />
    <PackageReference Include="lookbusy1344.RecordValueAnalyser" Version="1.3.1">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="Roslynator.Analyzers" Version="4.15.0">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>

  <ItemGroup>
    <Compile Include="../UnmanagedStringPool.cs" />
    <Compile Include="../PooledString.cs" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Restore packages**

Run: `dotnet restore Benchmarks/StringPoolBenchmarks.csproj`

Expected: `Restore succeeded.`

> If BenchmarkDotNet 0.14.0 is not found, check the latest version with `dotnet package search BenchmarkDotNet` and update the version in the csproj.

- [ ] **Step 3: Register with solution**

Run: `dotnet sln UnmanagedStringPool.sln add Benchmarks/StringPoolBenchmarks.csproj`

Expected: `Project 'Benchmarks/StringPoolBenchmarks.csproj' added to the solution.`

- [ ] **Step 4: Commit**

```bash
git add Benchmarks/StringPoolBenchmarks.csproj UnmanagedStringPool.sln
git commit -m "build: add Benchmarks project with BenchmarkDotNet reference"
```

---

## Task 2: Create the entry point

**Files:**
- Create: `Benchmarks/Program.cs`

- [ ] **Step 1: Create Program.cs**

```csharp
using BenchmarkDotNet.Running;
using LookBusy.Benchmarks;

BenchmarkSwitcher.FromTypes([
	typeof(BulkAllocateBenchmarks),
	typeof(InterleavedAllocFreeBenchmarks),
]).RunAll(args);
```

- [ ] **Step 2: Build to verify it compiles (will fail until benchmark classes exist)**

Run: `dotnet build Benchmarks/StringPoolBenchmarks.csproj --configuration Release`

Expected: build error referencing missing types `BulkAllocateBenchmarks` and `InterleavedAllocFreeBenchmarks` — this confirms the entry point wiring is correct and the missing types are the only gap.

---

## Task 3: Create BulkAllocateBenchmarks

**Files:**
- Create: `Benchmarks/BulkAllocateBenchmarks.cs`

This class benchmarks allocating N strings then freeing them all. The managed baseline allocates N `string` objects onto the managed heap; the pooled variant allocates N `PooledString` structs backed by unmanaged memory.

- [ ] **Step 1: Create BulkAllocateBenchmarks.cs**

```csharp
namespace LookBusy.Benchmarks;

using BenchmarkDotNet.Attributes;

[MemoryDiagnoser]
public class BulkAllocateBenchmarks
{
	[Params(100, 1_000, 10_000)]
	public int N { get; set; }

	[Params(8, 64, 256)]
	public int StringLength { get; set; }

	private string _source = "";
	private UnmanagedStringPool _pool = null!;

	[GlobalSetup]
	public void Setup()
	{
		_source = new string('x', StringLength);
		// 4x headroom: 2 bytes per char * StringLength * N * 4 overhead for metadata/alignment
		_pool = new UnmanagedStringPool(N * StringLength * sizeof(char) * 4);
	}

	[GlobalCleanup]
	public void Cleanup() => _pool.Dispose();

	[Benchmark(Baseline = true)]
	public string[] BulkAllocate_Managed()
	{
		var arr = new string[N];
		for (var i = 0; i < N; i++)
			arr[i] = new string('x', StringLength);
		return arr;
	}

	[Benchmark]
	public PooledString[] BulkAllocate_Pooled()
	{
		var arr = new PooledString[N];
		for (var i = 0; i < N; i++)
			arr[i] = _pool.Allocate(_source);
		for (var i = 0; i < N; i++)
			arr[i].Free();
		return arr;
	}
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Benchmarks/StringPoolBenchmarks.csproj --configuration Release`

Expected: build succeeds, one remaining error about `InterleavedAllocFreeBenchmarks` not found.

---

## Task 4: Create InterleavedAllocFreeBenchmarks

**Files:**
- Create: `Benchmarks/InterleavedAllocFreeBenchmarks.cs`

This class models a sliding window of 3 live strings: on each iteration, allocate a new string into a slot and free the string evicted from that slot. After N iterations, free remaining strings. This exercises the pool's free-block coalescing and fragmentation handling.

- [ ] **Step 1: Create InterleavedAllocFreeBenchmarks.cs**

```csharp
namespace LookBusy.Benchmarks;

using BenchmarkDotNet.Attributes;

[MemoryDiagnoser]
public class InterleavedAllocFreeBenchmarks
{
	private const int WindowSize = 3;

	[Params(100, 1_000, 10_000)]
	public int N { get; set; }

	[Params(8, 64, 256)]
	public int StringLength { get; set; }

	private string _source = "";
	private UnmanagedStringPool _pool = null!;

	[GlobalSetup]
	public void Setup()
	{
		_source = new string('x', StringLength);
		_pool = new UnmanagedStringPool(N * StringLength * sizeof(char) * 4);
	}

	[GlobalCleanup]
	public void Cleanup() => _pool.Dispose();

	[Benchmark(Baseline = true)]
	public string InterleavedAllocFree_Managed()
	{
		var window = new string[WindowSize];
		for (var i = 0; i < N; i++)
			window[i % WindowSize] = new string('x', StringLength);
		return window[(N - 1) % WindowSize];
	}

	[Benchmark]
	public PooledString InterleavedAllocFree_Pooled()
	{
		var window = new PooledString[WindowSize];
		for (var i = 0; i < N; i++) {
			var slot = i % WindowSize;
			if (i >= WindowSize)
				window[slot].Free();
			window[slot] = _pool.Allocate(_source);
		}
		var last = window[(N - 1) % WindowSize];
		var limit = Math.Min(N, WindowSize);
		for (var i = 0; i < limit; i++)
			window[i].Free();
		return last;
	}
}
```

- [ ] **Step 2: Build in Release and verify clean compile**

Run: `dotnet build Benchmarks/StringPoolBenchmarks.csproj --configuration Release`

Expected: `Build succeeded.` with 0 errors. Warnings about analyzer rules are acceptable; errors are not.

If analyzer errors appear (e.g. Roslynator or design rules), add suppressions at the top of the offending file:
```csharp
#pragma warning disable CA1724 // suppress if type name conflicts
```

- [ ] **Step 3: Commit**

```bash
git add Benchmarks/Program.cs Benchmarks/BulkAllocateBenchmarks.cs Benchmarks/InterleavedAllocFreeBenchmarks.cs
git commit -m "feat: add BenchmarkDotNet benchmarks for bulk and interleaved allocation patterns"
```

---

## Task 5: Smoke test — verify benchmark discovery and dry run

This confirms BenchmarkDotNet can discover and instantiate both classes without errors, before committing time to a full run.

- [ ] **Step 1: List discovered benchmarks**

Run: `dotnet run --configuration Release --project Benchmarks -- --list flat`

Expected output (order may vary):
```
BulkAllocateBenchmarks.BulkAllocate_Managed
BulkAllocateBenchmarks.BulkAllocate_Pooled
InterleavedAllocFreeBenchmarks.InterleavedAllocFree_Managed
InterleavedAllocFreeBenchmarks.InterleavedAllocFree_Pooled
```

If fewer than 4 methods appear, verify `[Benchmark]` attributes are present and classes are in the `LookBusy.Benchmarks` namespace.

- [ ] **Step 2: Run a single benchmark in dry-run mode to validate setup/cleanup**

Run: `dotnet run --configuration Release --project Benchmarks -- --filter "*BulkAllocate_Pooled*" --job dry`

Expected: BenchmarkDotNet runs one iteration of `BulkAllocate_Pooled` with N and StringLength at their first param values, prints a results table, exits 0.

If the pool runs out of space (throws `InvalidOperationException`), the GlobalSetup pool sizing formula needs increasing — multiply by 8 instead of 4.

- [ ] **Step 3: Commit**

No code changes in this task. If fixes were required to pass the dry run, commit those:

```bash
git add Benchmarks/
git commit -m "fix: correct pool sizing in benchmark GlobalSetup"
```

---

## Running Full Benchmarks

> This is not part of the implementation — it is a usage reference. Full runs take 10–30 minutes.

```bash
# Run everything
dotnet run --configuration Release --project Benchmarks -- --filter "*"

# Run only bulk allocation benchmarks
dotnet run --configuration Release --project Benchmarks -- --filter "*BulkAllocate*"

# Run only interleaved benchmarks
dotnet run --configuration Release --project Benchmarks -- --filter "*Interleaved*"
```

Results are exported to `BenchmarkDotNet.Artifacts/` in the `Benchmarks/` directory. The key columns to check are `Gen0`, `Gen1`, `Allocated`, and `Ratio`.

**Success criteria from spec:**
- `BulkAllocate_Pooled` Gen0 collections materially lower than `BulkAllocate_Managed` at N=10_000
- `InterleavedAllocFree_Pooled` Gen0 collections lower or zero vs managed
- Allocated bytes for pooled variants reflects struct overhead only (no per-string heap objects)
