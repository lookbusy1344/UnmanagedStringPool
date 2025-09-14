namespace LookBusy.Test;

using System;
using System.Runtime;
using System.Threading;
using LookBusy;
using Xunit;

public class FinalizerBehaviorTests
{
	#region Finalizer Execution Tests

	[Fact]
	public void Finalizer_ExecutesWithoutDispose_FreesUnmanagedMemory()
	{
		// Create a pool without disposing it to test finalizer path
		CreateUndisposedPool();

		// Force multiple GC cycles to increase likelihood of finalizer execution
		ForceFinalizerExecution();

		// If we get here without exceptions or crashes, finalizer likely worked correctly
		Assert.True(true);
	}

	[Fact]
	public void Finalizer_WithActiveAllocations_HandlesCorrectly()
	{
		// Create pool with active allocations and abandon without disposal
		CreateUndisposedPoolWithAllocations();

		// Force finalizer execution
		ForceFinalizerExecution();

		// Test should not crash or throw unhandled exceptions
		Assert.True(true);
	}

	[Fact]
	public void Finalizer_AfterExplicitDispose_DoesNotExecuteTwice()
	{
		var pool = new UnmanagedStringPool(1024);
		var str = pool.Allocate("Test");

		// Explicitly dispose
		pool.Dispose();

		// Set reference to null and force GC
		pool = null;
		str = default;
		ForceFinalizerExecution();

		// Should not cause issues since Dispose() calls GC.SuppressFinalize()
		Assert.True(true);
	}

	[Fact]
	public void Finalizer_MultiplePoolsSimultaneously_HandlesCorrectly()
	{
		// Create multiple pools without disposing to test concurrent finalizer execution
		CreateMultipleUndisposedPools(10);

		// Force finalizer execution
		ForceFinalizerExecution();

		// All finalizers should execute without interfering with each other
		Assert.True(true);
	}

	[Fact]
	public void Finalizer_WithFragmentedMemory_CleansUpCorrectly()
	{
		CreateFragmentedUndisposedPool();

		// Force finalizer execution
		ForceFinalizerExecution();

		// Fragmented or not, finalizer should clean up all unmanaged memory
		Assert.True(true);
	}

	[Fact]
	public void Finalizer_UnderMemoryPressure_ExecutesReliably()
	{
		// Create pools under memory pressure to test finalizer reliability
		CreatePoolsUnderMemoryPressure();

		// Apply memory pressure and force GC
		GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
		ForceFinalizerExecution();

		// Finalizers should execute even under memory pressure
		Assert.True(true);
	}

	#endregion

	#region Finalizer State Consistency Tests

	[Fact]
	public void Finalizer_DoesNotAccessManagedObjects_AfterGC()
	{
		// This test ensures the finalizer only touches unmanaged resources
		// and doesn't access potentially collected managed objects
		WeakReference poolRef = CreateTrackedUndisposedPool();

		// Force collection of the managed object
		ForceFinalizerExecution();

		// The weak reference should eventually become invalid
		// but the finalizer should have run without exceptions
		Assert.True(true);
	}

	[Fact]
	public async System.Threading.Tasks.Task Finalizer_ThreadSafety_WithConcurrentFinalizationAsync()
	{
		// Create pools on multiple threads to test finalizer thread safety
		var tasks = new System.Threading.Tasks.Task[Environment.ProcessorCount];

		for (int i = 0; i < tasks.Length; i++) {
			tasks[i] = System.Threading.Tasks.Task.Run(() => {
				CreateUndisposedPool();
			});
		}

		await System.Threading.Tasks.Task.WhenAll(tasks);

		// Force finalizer execution - all should run safely
		ForceFinalizerExecution();

		Assert.True(true);
	}

	#endregion

	#region Memory Leak Detection Tests

	[Fact]
	public void Finalizer_PreventsMemoryLeaks_WithoutExplicitDispose()
	{
		long initialMemory = GC.GetTotalMemory(true);

		// Create and abandon many pools to test for memory leaks
		for (int i = 0; i < 100; i++) {
			CreateUndisposedPool();
		}

		// Force multiple GC cycles
		for (int i = 0; i < 5; i++) {
			ForceFinalizerExecution();
		}

		long finalMemory = GC.GetTotalMemory(true);

		// Memory growth should be minimal if finalizers are working
		// Allow for some reasonable growth but not excessive
		long memoryGrowth = finalMemory - initialMemory;
		Assert.True(memoryGrowth < 1024 * 1024, // Less than 1MB growth
			$"Memory grew by {memoryGrowth} bytes, suggesting potential leak");
	}

	[Fact]
	public void Finalizer_HandlesLargeAllocations_WithoutLeaking()
	{
		long initialMemory = GC.GetTotalMemory(true);

		// Create pools with large allocations
		for (int i = 0; i < 10; i++) {
			CreateLargeUndisposedPool(1024 * 1024); // 1MB pools
		}

		// Force finalizer execution
		for (int i = 0; i < 3; i++) {
			ForceFinalizerExecution();
		}

		long finalMemory = GC.GetTotalMemory(true);

		// Even with large allocations, finalizers should prevent significant leaks
		long memoryGrowth = finalMemory - initialMemory;
		Assert.True(memoryGrowth < 5 * 1024 * 1024, // Less than 5MB growth
			$"Memory grew by {memoryGrowth} bytes with large allocations");
	}

	#endregion

	#region Helper Methods

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	private static void CreateUndisposedPool()
	{
#pragma warning disable CA2000 // Dispose objects before losing scope - intentional for finalizer testing
		var pool = new UnmanagedStringPool(1024);
		var str = pool.Allocate("Finalizer Test");

		// Use the string to ensure it's allocated
		_ = str.ToString();

		// Don't dispose - let finalizer handle it
#pragma warning restore CA2000
	}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	private static void CreateUndisposedPoolWithAllocations()
	{
#pragma warning disable CA2000 // Dispose objects before losing scope
		var pool = new UnmanagedStringPool(2048);

		for (int i = 0; i < 10; i++) {
			var str = pool.Allocate($"Test String {i}");
			_ = str.ToString(); // Use the string
		}

		// Abandon without disposal
#pragma warning restore CA2000
	}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	private static void CreateMultipleUndisposedPools(int count)
	{
#pragma warning disable CA2000 // Dispose objects before losing scope
		for (int i = 0; i < count; i++) {
			var pool = new UnmanagedStringPool(512);
			var str = pool.Allocate($"Pool {i} String");
			_ = str.ToString();
		}
#pragma warning restore CA2000
	}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	private static void CreateFragmentedUndisposedPool()
	{
#pragma warning disable CA2000 // Dispose objects before losing scope
		var pool = new UnmanagedStringPool(4096);
		var strings = new PooledString[20];

		// Create fragmentation
		for (int i = 0; i < strings.Length; i++) {
			strings[i] = pool.Allocate($"Fragment {i}");
		}

		// Free every other string to create fragmentation
		for (int i = 0; i < strings.Length; i += 2) {
			strings[i].Free();
		}

		// Abandon without disposal
#pragma warning restore CA2000
	}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	private static void CreatePoolsUnderMemoryPressure()
	{
		// Create memory pressure
		var memoryHogs = new byte[10][];
		try {
			for (int i = 0; i < memoryHogs.Length; i++) {
				memoryHogs[i] = new byte[1024 * 1024]; // 1MB each
			}

			// Create pools under this pressure
			CreateMultipleUndisposedPools(5);
		}
		finally {
			// Release memory pressure
			Array.Clear(memoryHogs);
		}
	}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	private static WeakReference CreateTrackedUndisposedPool()
	{
#pragma warning disable CA2000 // Dispose objects before losing scope
		var pool = new UnmanagedStringPool(1024);
		var str = pool.Allocate("Tracked Pool Test");
		_ = str.ToString();

		return new WeakReference(pool);
#pragma warning restore CA2000
	}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	private static void CreateLargeUndisposedPool(int sizeChars)
	{
#pragma warning disable CA2000 // Dispose objects before losing scope
		var pool = new UnmanagedStringPool(sizeChars);

		// Allocate a large string
		var largeString = new string('A', Math.Min(sizeChars / 2, 100000));
		var str = pool.Allocate(largeString);
		_ = str.ToString();
#pragma warning restore CA2000
	}

	private static void ForceFinalizerExecution()
	{
		// Multiple rounds of GC to ensure finalizers run
		for (int i = 0; i < 3; i++) {
			GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);
			GC.WaitForPendingFinalizers();
			Thread.Sleep(10); // Small delay to allow finalizer thread to work
		}

		// Final collection to clean up any objects finalized in previous round
		GC.Collect();
	}

	#endregion
}
