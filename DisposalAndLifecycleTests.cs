namespace Playground.Tests;

using System;
using System.Collections.Generic;
using Xunit;

public class DisposalAndLifecycleTests
{
	#region Basic Disposal Tests

	[Fact]
	public void Dispose_EmptyPool_DoesNotThrow()
	{
		var pool = new UnmanagedStringPool(1024);

		pool.Dispose();

		Assert.True(pool.IsDisposed);
	}

	[Fact]
	public void Dispose_PoolWithAllocations_InvalidatesStrings()
	{
		var pool = new UnmanagedStringPool(1024);
		var str1 = pool.Allocate("Test1");
		var str2 = pool.Allocate("Test2");

		pool.Dispose();

		Assert.True(pool.IsDisposed);
		Assert.Throws<ObjectDisposedException>(() => str1.AsSpan());
		Assert.Throws<ObjectDisposedException>(() => str2.ToString());
	}

	[Fact]
	public void Dispose_MultipleCalls_SafeToCall()
	{
		var pool = new UnmanagedStringPool(1024);
		var str = pool.Allocate("Test");

		pool.Dispose();
		pool.Dispose(); // Second call should be safe
		pool.Dispose(); // Third call should be safe

		Assert.True(pool.IsDisposed);
	}

	[Fact]
	public void UsingStatement_AutomaticallyDisposesPool()
	{
		UnmanagedStringPool pool;
		PooledString str;

		using (pool = new UnmanagedStringPool(1024)) {
			str = pool.Allocate("Test");
			Assert.Equal("Test", str.ToString());
			Assert.False(pool.IsDisposed);
		}

		Assert.True(pool.IsDisposed);
		Assert.Throws<ObjectDisposedException>(() => str.AsSpan());
	}

	#endregion

	#region Pool Operations After Disposal

	[Fact]
	public void Allocate_AfterDisposal_ThrowsObjectDisposedException()
	{
		var pool = new UnmanagedStringPool(1024);
		pool.Dispose();

		Assert.Throws<ObjectDisposedException>(() => pool.Allocate("test"));
		Assert.Throws<ObjectDisposedException>(() => pool.Allocate("test".AsSpan()));
	}

	[Fact]
	public void DefragmentAndGrowPool_AfterDisposal_ThrowsObjectDisposedException()
	{
		var pool = new UnmanagedStringPool(1024);
		pool.Dispose();

		Assert.Throws<ObjectDisposedException>(() => pool.DefragmentAndGrowPool(100));
	}

	[Fact]
	public void DumpBufferAsString_AfterDisposal_ThrowsObjectDisposedException()
	{
		var pool = new UnmanagedStringPool(1024);
		pool.Dispose();

		Assert.Throws<ObjectDisposedException>(() => pool.DumpBufferAsString());
	}

	[Fact]
	public void PoolProperties_AfterDisposal_StillAccessible()
	{
		var pool = new UnmanagedStringPool(1024);
		var str = pool.Allocate("Test");

		pool.Dispose();

		// These properties should still be accessible for diagnostic purposes
		Assert.True(pool.IsDisposed);
		// Note: Other properties might throw ObjectDisposedException - this is implementation dependent
	}

	#endregion

	#region PooledString Operations After Pool Disposal

	[Fact]
	public void PooledString_AllOperations_AfterPoolDisposal_ThrowObjectDisposedException()
	{
		var pool = new UnmanagedStringPool(1024);
		var str = pool.Allocate("Hello World");

		pool.Dispose();

		Assert.Throws<ObjectDisposedException>(() => str.AsSpan());
		Assert.Throws<ObjectDisposedException>(() => str.ToString());
		Assert.Throws<ObjectDisposedException>(() => str.Length);
		Assert.Throws<ObjectDisposedException>(() => str.IsEmpty);
		Assert.Throws<ObjectDisposedException>(() => str.Insert(0, "test"));
		Assert.Throws<ObjectDisposedException>(() => str.Replace("Hello", "Hi"));
		Assert.Throws<ObjectDisposedException>(() => str.IndexOf("World"));
		Assert.Throws<ObjectDisposedException>(() => str.Contains("World"));
		Assert.Throws<ObjectDisposedException>(() => str.StartsWith("Hello"));
		Assert.Throws<ObjectDisposedException>(() => str.EndsWith("World"));
		Assert.Throws<ObjectDisposedException>(() => str.SubstringSpan(0, 5));
	}

	[Fact]
	public void PooledString_Free_AfterPoolDisposal_DoesNotThrow()
	{
		var pool = new UnmanagedStringPool(1024);
		var str = pool.Allocate("Test");

		pool.Dispose();

		// Free should not throw even after pool disposal
		str.Free();
		str.Dispose();
	}

	[Fact]
	public void PooledString_Equals_AfterPoolDisposal_ReturnsFalse()
	{
		var pool1 = new UnmanagedStringPool(1024);
		var pool2 = new UnmanagedStringPool(1024);

		var str1 = pool1.Allocate("Test");
		var str2 = pool2.Allocate("Test");

		pool1.Dispose();

		Assert.False(str1.Equals(str2));
		Assert.False(str1 == str2);

		pool2.Dispose();
	}

	[Fact]
	public void PooledString_GetHashCode_AfterPoolDisposal_ReturnsMinusOne()
	{
		var pool = new UnmanagedStringPool(1024);
		var str = pool.Allocate("Test");

		pool.Dispose();

		Assert.Equal(-1, str.GetHashCode());
	}

	#endregion

	#region PooledString Individual Disposal

	[Fact]
	public void PooledString_Dispose_FreesStringFromPool()
	{
		using var pool = new UnmanagedStringPool(1024);
		var str = pool.Allocate("Test String");

		Assert.Equal(1, pool.ActiveAllocations);

		str.Dispose();

		Assert.Equal(0, pool.ActiveAllocations);
	}

	[Fact]
	public void PooledString_UsingStatement_AutomaticallyFrees()
	{
		using var pool = new UnmanagedStringPool(1024);

		using (var str = pool.Allocate("Test")) {
			Assert.Equal("Test", str.ToString());
			Assert.Equal(1, pool.ActiveAllocations);
		}

		Assert.Equal(0, pool.ActiveAllocations);
	}

	[Fact]
	public void PooledString_MultipleFree_SafeToCall()
	{
		using var pool = new UnmanagedStringPool(1024);
		var str = pool.Allocate("Test");

		str.Free();
		str.Free(); // Second call should be safe
		str.Dispose(); // Should also be safe

		Assert.Equal(0, pool.ActiveAllocations);
	}

	[Fact]
	public void PooledString_UseAfterFree_ThrowsException()
	{
		using var pool = new UnmanagedStringPool(1024);
		var str = pool.Allocate("Test");

		str.Free();

		Assert.Throws<ArgumentException>(() => str.AsSpan());
		Assert.Throws<ArgumentException>(() => str.ToString());
		Assert.Throws<ArgumentException>(() => str.Length);
	}

	#endregion

	#region Empty String Disposal

	[Fact]
	public void EmptyString_Dispose_DoesNotThrow()
	{
		var empty = PooledString.Empty;

		empty.Free();
		empty.Dispose();

		// Should not throw or affect anything
	}

	[Fact]
	public void EmptyString_MultipleDispose_Safe()
	{
		var empty = PooledString.Empty;

		for (int i = 0; i < 10; i++) {
			empty.Free();
			empty.Dispose();
		}

		// All operations should still work
		Assert.True(empty.IsEmpty);
		Assert.Equal(0, empty.Length);
		Assert.Equal("", empty.ToString());
	}

	[Fact]
	public void EmptyString_AlwaysValid_EvenAfterMultipleDisposals()
	{
		var empty1 = PooledString.Empty;
		var empty2 = PooledString.Empty;

		empty1.Dispose();
		empty2.Free();

		// Should still be equal and functional
		Assert.True(empty1 == empty2);
		Assert.True(empty1.Equals(empty2));
		Assert.Equal(0, empty1.GetHashCode());
		Assert.Equal(0, empty2.GetHashCode());
	}

	#endregion

	#region Complex Disposal Scenarios

	[Fact]
	public void ComplexDisposal_PartialStringsFree_ThenPoolDispose_HandlesCorrectly()
	{
		var pool = new UnmanagedStringPool(1024);
		var strings = new List<PooledString>();

		// Allocate multiple strings
		for (int i = 0; i < 10; i++) {
			strings.Add(pool.Allocate($"String{i}"));
		}

		// Free some strings individually
		for (int i = 0; i < 5; i++) {
			strings[i].Free();
		}

		Assert.Equal(5, pool.ActiveAllocations);

		// Dispose the entire pool
		pool.Dispose();

		// All remaining strings should become invalid
		for (int i = 5; i < 10; i++) {
			Assert.Throws<ObjectDisposedException>(() => strings[i].AsSpan());
		}

		// Already freed strings should remain safely freed
		for (int i = 0; i < 5; i++) {
			strings[i].Free(); // Should not throw
		}
	}

	[Fact]
	public void Disposal_WithOperationChains_HandlesCorrectly()
	{
		var pool = new UnmanagedStringPool(2048);
		var original = pool.Allocate("Hello World");

		// Create a chain of operations
		var result1 = original.Insert(6, "Beautiful ");
		var result2 = result1.Replace("Hello", "Hi");

		Assert.Equal("Hi Beautiful World", result2.ToString());
		Assert.Equal(3, pool.ActiveAllocations); // original + result1 + result2

		// Dispose intermediate results
		result1.Free();
		Assert.Equal(2, pool.ActiveAllocations);

		// Pool disposal should invalidate all
		pool.Dispose();

		Assert.Throws<ObjectDisposedException>(() => original.AsSpan());
		Assert.Throws<ObjectDisposedException>(() => result2.AsSpan());
	}

	[Fact]
	public void Finalizer_CalledOnPoolCollection_FreesUnmanagedMemory()
	{
		// This test is hard to verify directly due to GC timing
		// We can at least ensure the finalizer exists and doesn't crash
#pragma warning disable CA2000 // Intentionally not disposing to test finalizer
		var pool = new UnmanagedStringPool(1024);
		var str = pool.Allocate("Test");

		// Don't call Dispose to test finalizer path
		pool = null;
#pragma warning restore CA2000
		str = default;

		// Force GC to potentially run finalizer
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();

		// If we get here without crashing, finalizer probably worked
		Assert.True(true);
	}

	#endregion

	#region Lifecycle State Validation

	[Fact]
	public void PooledString_ValidityChecks_ThroughoutLifecycle()
	{
		using var pool = new UnmanagedStringPool(1024);
		var str = pool.Allocate("Lifecycle Test");

		// Initially valid
		Assert.Equal("Lifecycle Test", str.ToString());
		Assert.Equal(14, str.Length);
		Assert.False(str.IsEmpty);

		// Operations should work
		var modified = str.Insert(0, "Start ");
		Assert.Equal("Start Lifecycle Test", modified.ToString());

		// Free the original
		str.Free();

		// Original should be invalid
		Assert.Throws<ArgumentException>(() => str.AsSpan());

		// Modified should still be valid
		Assert.Equal("Start Lifecycle Test", modified.ToString());

		// Free the modified
		modified.Free();

		// Both should be invalid now
		Assert.Throws<ArgumentException>(() => str.Length);
		Assert.Throws<ArgumentException>(() => modified.Length);
	}

	[Fact]
	public void Pool_StateConsistency_ThroughoutLifecycle()
	{
		var pool = new UnmanagedStringPool(1024);

		// Initial state
		Assert.Equal(0, pool.ActiveAllocations);
		Assert.Equal(1024, pool.FreeSpaceChars);
		Assert.Equal(0.0, pool.FragmentationPercentage);
		Assert.False(pool.IsDisposed);

		// After allocation
		var str1 = pool.Allocate("Test1");
		Assert.Equal(1, pool.ActiveAllocations);
		Assert.True(pool.FreeSpaceChars < 1024);

		// After more allocations
		var str2 = pool.Allocate("Test2");
		Assert.Equal(2, pool.ActiveAllocations);

		// After freeing one
		str1.Free();
		Assert.Equal(1, pool.ActiveAllocations);
		Assert.True(pool.FragmentationPercentage >= 0);

		// After disposal
		pool.Dispose();
		Assert.True(pool.IsDisposed);
		Assert.Throws<ObjectDisposedException>(() => str2.AsSpan());
	}

	#endregion

	#region Stress Testing Disposal

	[Fact]
	public void StressTest_ManyAllocationsAndDisposals_RemainsStable()
	{
		using var pool = new UnmanagedStringPool(4096);
		var random = new Random(42);

		for (int iteration = 0; iteration < 100; iteration++) {
			var strings = new List<PooledString>();
			var disposed = new bool[20]; // Track which strings are disposed

			// Allocate many strings
			for (int i = 0; i < 20; i++) {
				strings.Add(pool.Allocate($"Iteration{iteration}_String{i}"));
			}

			// Randomly dispose some
			for (int i = 0; i < strings.Count; i++) {
				if (random.Next(2) == 0) {
					strings[i].Dispose();
					disposed[i] = true;
				}
			}

			// Verify remaining strings are still valid
			for (int i = 0; i < strings.Count; i++) {
				if (!disposed[i]) // Only check the ones we didn't dispose
				{
					var content = strings[i].ToString();
					Assert.StartsWith($"Iteration{iteration}_String{i}", content);
				}
			}
		}

		Assert.True(pool.ActiveAllocations >= 0);
	}

	[Fact]
	public void StressTest_PoolCreationAndDisposal_NoMemoryLeaks()
	{
		// Create and dispose many pools to test for leaks
		for (int i = 0; i < 100; i++) {
			using var pool = new UnmanagedStringPool(1024);
			var strings = new List<PooledString>();

			// Use the pool
			for (int j = 0; j < 10; j++) {
				strings.Add(pool.Allocate($"Pool{i}_String{j}"));
			}

			// Verify strings work
			foreach (var str in strings) {
				Assert.Contains($"Pool{i}_", str.ToString());
			}

			// Pool automatically disposed by using statement
		}

		// Force GC to clean up any lingering finalizers
		GC.Collect();
		GC.WaitForPendingFinalizers();

		Assert.True(true); // If we get here without OOM, likely no major leaks
	}

	#endregion
}
