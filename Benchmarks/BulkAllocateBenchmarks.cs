#pragma warning disable CA1001 // BenchmarkDotNet manages disposal via [GlobalCleanup]
namespace LookBusy.Benchmarks;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

[ShortRunJob]
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
		_pool = new UnmanagedStringPool(N * StringLength * sizeof(char) * 4);
	}

	[GlobalCleanup]
	public void Cleanup() => _pool.Dispose();

	[Benchmark(Baseline = true)]
	public string[] BulkAllocate_Managed()
	{
		var arr = new string[N];
		for (var i = 0; i < N; i++) {
			arr[i] = new string('x', StringLength);
		}
		return arr;
	}

	[Benchmark]
	public PooledString[] BulkAllocate_Pooled()
	{
		var arr = new PooledString[N];
		for (var i = 0; i < N; i++) {
			arr[i] = _pool.Allocate(_source);
		}
		for (var i = 0; i < N; i++) {
			arr[i].Free();
		}
		return arr;
	}
}
