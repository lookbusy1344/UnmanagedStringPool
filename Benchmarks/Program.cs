namespace LookBusy.Benchmarks;

using BenchmarkDotNet.Running;

public static class BenchmarkProgram
{
	public static void Main(string[] args) =>
		BenchmarkSwitcher.FromTypes([
			typeof(BulkAllocateBenchmarks),
			typeof(InterleavedAllocFreeBenchmarks),
		]).Run(args);
}
