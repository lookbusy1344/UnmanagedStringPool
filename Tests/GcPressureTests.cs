namespace LookBusy.Test;

using System;
using LookBusy;
using Xunit;

public sealed class GcPressureTests
{
	private const int N = 10_000;
	private const int LargeStringLength = 256;
	private const int WindowSize = 3;

	[Fact]
	public void BulkAllocate_LargeStrings_PooledAllocatesFarLessManagedMemory()
	{
		var source = new string('x', LargeStringLength);

		var managedBytes = MeasureAllocated(() => {
			var arr = new string[N];
			for (var i = 0; i < N; i++) {
				arr[i] = new string('x', LargeStringLength);
			}
			GC.KeepAlive(arr);
		});

		using var pool = new UnmanagedStringPool(N * LargeStringLength * sizeof(char) * 4);
		var pooledBytes = MeasureAllocated(() => {
			var arr = new PooledString[N];
			for (var i = 0; i < N; i++) {
				arr[i] = pool.Allocate(source);
			}
			for (var i = 0; i < N; i++) {
				arr[i].Free();
			}
			GC.KeepAlive(arr);
		});

		// Cold-pool test: pooled allocates ~30% of managed (dict/freelist bookkeeping overhead).
		// Pre-warmed (benchmark) shows ~8%. Assert <50% to cover both cases.
		Assert.True(pooledBytes < managedBytes / 2,
			$"Pooled ({pooledBytes:N0} B) should be <1/2 of managed ({managedBytes:N0} B)");
	}

	[Fact]
	public void InterleavedAllocFree_LargeStrings_PooledAllocatesFarLessManagedMemory()
	{
		var source = new string('x', LargeStringLength);

		var managedBytes = MeasureAllocated(() => {
			var window = new string[WindowSize];
			for (var i = 0; i < N; i++) {
				window[i % WindowSize] = new string('x', LargeStringLength);
			}
			GC.KeepAlive(window);
		});

		using var pool = new UnmanagedStringPool(N * LargeStringLength * sizeof(char) * 4);
		var pooledBytes = MeasureAllocated(() => {
			var window = new PooledString[WindowSize];
			for (var i = 0; i < N; i++) {
				var slot = i % WindowSize;
				if (i >= WindowSize) {
					window[slot].Free();
				}
				window[slot] = pool.Allocate(source);
			}
			var limit = Math.Min(N, WindowSize);
			for (var i = 0; i < limit; i++) {
				window[i].Free();
			}
			GC.KeepAlive(window);
		});

		// Benchmarks show pooled allocates ~16% of managed at this scale; assert <25% with margin
		Assert.True(pooledBytes < managedBytes / 4,
			$"Pooled ({pooledBytes:N0} B) should be <1/4 of managed ({managedBytes:N0} B)");
	}

	private static long MeasureAllocated(Action action)
	{
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
		var before = GC.GetAllocatedBytesForCurrentThread();
		action();
		return GC.GetAllocatedBytesForCurrentThread() - before;
	}
}
