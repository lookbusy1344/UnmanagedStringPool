#pragma warning disable CA1001 // BenchmarkDotNet manages disposal via [GlobalCleanup]
namespace LookBusy.Benchmarks;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

[ShortRunJob]
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
		for (var i = 0; i < N; i++) {
			window[i % WindowSize] = new string('x', StringLength);
		}
		return window[(N - 1) % WindowSize];
	}

	[Benchmark]
	public PooledString InterleavedAllocFree_Pooled()
	{
		var window = new PooledString[WindowSize];
		for (var i = 0; i < N; i++) {
			var slot = i % WindowSize;
			if (i >= WindowSize) {
				window[slot].Free();
			}
			window[slot] = _pool.Allocate(_source);
		}
		var last = window[(N - 1) % WindowSize];
		var limit = Math.Min(N, WindowSize);
		for (var i = 0; i < limit; i++) {
			window[i].Free();
		}
		return last;
	}
}
