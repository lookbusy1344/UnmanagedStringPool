#pragma warning disable CA1001 // BenchmarkDotNet manages disposal via [GlobalCleanup]
namespace LookBusy.Benchmarks;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

[SimpleJob(launchCount: 1, warmupCount: 1, iterationCount: 3)]
[MemoryDiagnoser]
public class BulkAllocateBenchmarks
{
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
		_segmented.Reserve(N * StringLength);
	}

	[GlobalCleanup]
	public void Cleanup()
	{
		_legacy.Dispose();
		_segmented.Dispose();
	}

	[Benchmark(Baseline = true)]
	public string[] BulkAllocate_Managed()
	{
		var arr = new string[N];
		for (var i = 0; i < N; ++i) {
			arr[i] = new string('x', StringLength);
		}
		return arr;
	}

	[Benchmark]
	public PooledString[] BulkAllocate_Legacy()
	{
		var arr = new PooledString[N];
		for (var i = 0; i < N; ++i) {
			arr[i] = _legacy.Allocate(_source);
		}
		for (var i = 0; i < N; ++i) {
			arr[i].Free();
		}
		return arr;
	}

	[Benchmark]
	public PooledStringRef[] BulkAllocate_Segmented()
	{
		var arr = new PooledStringRef[N];
		for (var i = 0; i < N; ++i) {
			arr[i] = _segmented.Allocate(_source);
		}
		for (var i = 0; i < N; ++i) {
			arr[i].Free();
		}
		return arr;
	}
}
