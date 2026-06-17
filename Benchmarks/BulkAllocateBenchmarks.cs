#pragma warning disable CA1001 // BenchmarkDotNet manages disposal via [GlobalCleanup]
namespace LookBusy.Benchmarks;

using BenchmarkDotNet.Attributes;

[SimpleJob(1, 1, 3)]
[MemoryDiagnoser]
public class BulkAllocateBenchmarks
{
	private UnmanagedStringPool _legacy = null!;
	private SegmentedStringPool _segmented = null!;

	private string _source = "";

	[Params(1_000, 10_000)] public int N { get; set; }

	[Params(8, 256)] public int StringLength { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		_source = new('x', StringLength);
		_legacy = new(N * StringLength * sizeof(char) * 4);
		_segmented = new();
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
			arr[i] = new('x', StringLength);
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
