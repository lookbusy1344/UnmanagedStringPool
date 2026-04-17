#pragma warning disable CA1001 // BenchmarkDotNet manages disposal via [GlobalCleanup]
namespace LookBusy.Benchmarks;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

[SimpleJob(launchCount: 1, warmupCount: 1, iterationCount: 3)]
[MemoryDiagnoser]
public class InterleavedAllocFreeBenchmarks
{
	private const int WindowSize = 3;

	[Params(1_000, 10_000)]
	public int N { get; set; }

	[Params(8, 256)]
	public int StringLength { get; set; }

	private string _source = "";
	private UnmanagedStringPool _legacy = null!;
	private SegmentedStringPool _segmented = null!;

	[GlobalSetup]
	public void Setup()
	{
		_source = new string('x', StringLength);
		_legacy = new UnmanagedStringPool(N * StringLength * sizeof(char) * 4);
		_segmented = new SegmentedStringPool();
		_segmented.Reserve(WindowSize * StringLength * 4);
	}

	[GlobalCleanup]
	public void Cleanup()
	{
		_legacy.Dispose();
		_segmented.Dispose();
	}

	[Benchmark(Baseline = true)]
	public string InterleavedAllocFree_Managed()
	{
		var window = new string[WindowSize];
		for (var i = 0; i < N; i++) {
			window[i % WindowSize] = new string('x', StringLength);
		}
		return window[(N - 1) % WindowSize];
	}

	[Benchmark]
	public PooledString InterleavedAllocFree_Legacy()
	{
		var window = new PooledString[WindowSize];
		for (var i = 0; i < N; i++) {
			var slot = i % WindowSize;
			if (i >= WindowSize) {
				window[slot].Free();
			}
			window[slot] = _legacy.Allocate(_source);
		}
		var last = window[(N - 1) % WindowSize];
		var limit = Math.Min(N, WindowSize);
		for (var i = 0; i < limit; i++) {
			window[i].Free();
		}
		return last;
	}

	[Benchmark]
	public PooledStringRef InterleavedAllocFree_Segmented()
	{
		var window = new PooledStringRef[WindowSize];
		for (var i = 0; i < N; i++) {
			var slot = i % WindowSize;
			if (i >= WindowSize) {
				window[slot].Free();
			}
			window[slot] = _segmented.Allocate(_source);
		}
		var last = window[(N - 1) % WindowSize];
		var limit = Math.Min(N, WindowSize);
		for (var i = 0; i < limit; i++) {
			window[i].Free();
		}
		return last;
	}
}
