namespace LookBusy.Test;

using System;
using System.Collections.Generic;
using System.Linq;
using LookBusy;
using Xunit;

public sealed class UnmanagedStringPoolTests : IDisposable
{
	private readonly UnmanagedStringPool pool;

	public UnmanagedStringPoolTests() => pool = new(1024);

	public void Dispose()
	{
		pool?.Dispose();
		GC.SuppressFinalize(this);
	}

	#region Construction Tests

	[Fact]
	public void Constructor_ValidCapacity_InitializesCorrectly()
	{
		using var testPool = new UnmanagedStringPool(512);

		Assert.Equal(512, testPool.FreeSpaceChars);
		Assert.Equal(512, testPool.EndBlockSizeChars);
		Assert.Equal(0, testPool.ActiveAllocations);
		Assert.Equal(0.0, testPool.FragmentationPercentage);
		Assert.True(testPool.AllowGrowth);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	[InlineData(-100)]
	public void Constructor_InvalidCapacity_ThrowsArgumentOutOfRangeException(int capacity) =>
		Assert.Throws<ArgumentOutOfRangeException>(() => new UnmanagedStringPool(capacity));

	[Fact]
	public void Constructor_WithGrowthDisabled_SetsAllowGrowthCorrectly()
	{
		using var testPool = new UnmanagedStringPool(512, false);

		Assert.False(testPool.AllowGrowth);
	}

	#endregion

	#region Basic Allocation Tests

	[Fact]
	public void Allocate_SimpleString_ReturnsValidPooledString()
	{
		var result = pool.Allocate("Hello");

		Assert.Equal("Hello", result.ToString());
		Assert.Equal(5, result.Length);
		Assert.False(result.IsEmpty);
		Assert.Equal(1, pool.ActiveAllocations);
	}

	[Fact]
	public void Allocate_EmptyString_ReturnsEmptyPooledString()
	{
		var result = pool.Allocate("");

		Assert.True(result.IsEmpty);
		Assert.Equal(0, result.Length);
		Assert.Equal("", result.ToString());
		Assert.Equal(0, pool.ActiveAllocations); // Empty strings don't count as allocations
	}

	[Fact]
	public void Allocate_ReadOnlySpan_WorksCorrectly()
	{
		var span = "Test String".AsSpan();
		var result = pool.Allocate(span);

		Assert.Equal("Test String", result.ToString());
		Assert.Equal(11, result.Length);
	}

	[Fact]
	public void Allocate_MultipleStrings_TracksCorrectly()
	{
		var str1 = pool.Allocate("First");
		var str2 = pool.Allocate("Second");
		var str3 = pool.Allocate("Third");

		Assert.Equal("First", str1.ToString());
		Assert.Equal("Second", str2.ToString());
		Assert.Equal("Third", str3.ToString());
		Assert.Equal(3, pool.ActiveAllocations);
	}

	#endregion

	#region Memory Management Tests

	[Fact]
	public void FreeString_SingleString_UpdatesPoolState()
	{
		var str = pool.Allocate("Hello World");
		var initialFreeSpace = pool.FreeSpaceChars;

		str.Free();

		Assert.True(pool.FreeSpaceChars >= initialFreeSpace);
		Assert.Equal(0, pool.ActiveAllocations);
	}

	[Fact]
	public void FreeString_MultipleStrings_HandlesCorrectly()
	{
		var str1 = pool.Allocate("First");
		var str2 = pool.Allocate("Second");
		var str3 = pool.Allocate("Third");

		str2.Free();

		Assert.Equal(2, pool.ActiveAllocations);
		Assert.Equal("First", str1.ToString());
		Assert.Equal("Third", str3.ToString());
	}

	[Fact]
	public void FreeString_ReuseSpace_WorksCorrectly()
	{
		var str1 = pool.Allocate("Hello");
		str1.Free();

		var str2 = pool.Allocate("World"); // Should reuse the freed space

		Assert.Equal("World", str2.ToString());
		Assert.Equal(1, pool.ActiveAllocations);
	}

	[Fact]
	public void Dispose_UsingStatement_AutomaticallyFreesString()
	{
		using var str = pool.Allocate("Test");
		Assert.Equal(1, pool.ActiveAllocations);
		// String should be automatically freed when using block ends
	}

	#endregion

	#region Pool Growth and Defragmentation Tests

	[Fact]
	public void DefragmentAndGrowPool_ValidInput_IncreasesCapacity()
	{
		var str1 = pool.Allocate("Keep this");
		var str2 = pool.Allocate("Free this");
		str2.Free();

		var initialCapacity = pool.FreeSpaceChars + (pool.ActiveAllocations * "Keep this".Length);

		pool.DefragmentAndGrowPool(512);

		Assert.True(pool.FreeSpaceChars > initialCapacity);
		Assert.Equal("Keep this", str1.ToString()); // Should still be valid after defrag
	}

	[Fact]
	public void DefragmentAndGrowPool_NegativeBytes_ThrowsException() =>
		Assert.Throws<ArgumentOutOfRangeException>(() => pool.DefragmentAndGrowPool(-100));

	[Fact]
	public void AllowGrowth_WhenFalse_ThrowsOnPoolExhaustion()
	{
		using var smallPool = new UnmanagedStringPool(10, false);

		// Fill the pool
		var str1 = smallPool.Allocate("12345");

		// This should throw since growth is disabled and pool is nearly full
		_ = Assert.Throws<OutOfMemoryException>(() => smallPool.Allocate("This string is too long for remaining space"));
	}

	[Fact]
	public void AllowGrowth_WhenTrue_AutomaticallyGrowsPool()
	{
		using var smallPool = new UnmanagedStringPool(10, true);

		// Fill the pool
		var str1 = smallPool.Allocate("12345");
		var str2 = smallPool.Allocate("This string should trigger growth");

		Assert.Equal("12345", str1.ToString());
		Assert.Equal("This string should trigger growth", str2.ToString());
		Assert.Equal(2, smallPool.ActiveAllocations);
	}

	#endregion

	#region Edge Cases and Error Handling

	[Fact]
	public void Allocate_AfterDisposal_ThrowsObjectDisposedException()
	{
		var testPool = new UnmanagedStringPool(512);
		testPool.Dispose();

		_ = Assert.Throws<ObjectDisposedException>(() => testPool.Allocate("test"));
	}

	[Fact]
	public void AsSpan_AfterPoolDisposal_ThrowsObjectDisposedException()
	{
		var testPool = new UnmanagedStringPool(512);
		var str = testPool.Allocate("test");
		testPool.Dispose();

		_ = Assert.Throws<ObjectDisposedException>(() => str.AsSpan());
	}

	[Fact]
	public void FreeString_EmptyString_NoEffect()
	{
		var emptyStr = pool.CreateEmptyString();

		// Should not throw or cause issues
		emptyStr.Free();
		emptyStr.Dispose();
	}

	[Fact]
	public void FreeString_AlreadyFreedString_NoEffect()
	{
		var str = pool.Allocate("test");
		str.Free();

		// Freeing again should not cause issues
		str.Free();
		str.Dispose();
	}

	[Fact]
	public void DumpBufferAsString_AfterDisposal_ThrowsObjectDisposedException()
	{
		var testPool = new UnmanagedStringPool(512);
		testPool.Dispose();

		_ = Assert.Throws<ObjectDisposedException>(() => testPool.DumpBufferAsString());
	}

	#endregion

	#region Diagnostic and State Tests

	[Fact]
	public void DumpBufferAsString_EmptyPool_ReturnsEmptyString()
	{
		var dump = pool.DumpBufferAsString();

		Assert.Equal(string.Empty, dump);
	}

	[Fact]
	public void DumpBufferAsString_WithAllocations_ContainsData()
	{
		var str1 = pool.Allocate("Hello");
		var str2 = pool.Allocate("World");

		var dump = pool.DumpBufferAsString();

		Assert.Contains("Hello", dump);
		Assert.Contains("World", dump);
	}

	[Fact]
	public void FragmentationPercentage_CalculatesCorrectly()
	{
		// Create some fragmentation
		var str1 = pool.Allocate("First");
		var str2 = pool.Allocate("Second");
		var str3 = pool.Allocate("Third");

		str2.Free(); // Create a gap in the middle

		Assert.True(pool.FragmentationPercentage >= 0);
		Assert.True(pool.FragmentationPercentage <= 100);
	}

	[Fact]
	public void FreeSpaceChars_AccuratelyReflectsAvailableSpace()
	{
		var initialFreeSpace = pool.FreeSpaceChars;
		var str = pool.Allocate("Hello");

		// Free space should decrease
		Assert.True(pool.FreeSpaceChars < initialFreeSpace);

		str.Free();

		// Free space should increase (though might not be exactly equal due to alignment)
		Assert.True(pool.FreeSpaceChars >= initialFreeSpace - 10); // Allow for alignment overhead
	}

	#endregion

	#region Stress Tests

	[Fact]
	public void Allocate_ManySmallStrings_HandlesCorrectly()
	{
		var strings = new List<PooledString>();

		for (var i = 0; i < 100; i++) {
			strings.Add(pool.Allocate($"String {i}"));
		}

		Assert.Equal(100, pool.ActiveAllocations);

		// Verify all strings are correct
		for (var i = 0; i < 100; i++) {
			Assert.Equal($"String {i}", strings[i].ToString());
		}
	}

	[Fact]
	public void Allocate_LargeString_HandlesCorrectly()
	{
		var largeString = new string('A', 10000);
		var str = pool.Allocate(largeString);

		Assert.Equal(largeString, str.ToString());
		Assert.Equal(10000, str.Length);
	}

	[Fact]
	public void AllocateAndFree_RandomPattern_MaintainsConsistency()
	{
		var random = new Random(42); // Fixed seed for reproducibility
		var activeStrings = new List<PooledString>();

		for (var i = 0; i < 200; i++) {
			if (activeStrings.Count == 0 || random.Next(3) == 0) // Allocate
			{
				var content = $"String_{i}_{random.Next(1000)}";
				activeStrings.Add(pool.Allocate(content));
			} else // Free
			{
				var index = random.Next(activeStrings.Count);
				activeStrings[index].Free();
				activeStrings.RemoveAt(index);
			}
		}

		Assert.Equal(activeStrings.Count, pool.ActiveAllocations);

		// Verify remaining strings are still valid
		foreach (var str in activeStrings) {
			_ = str.ToString(); // Should not throw
		}
	}

	#endregion
}
