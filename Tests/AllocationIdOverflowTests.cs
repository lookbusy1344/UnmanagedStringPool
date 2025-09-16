namespace LookBusy.Test;

using System;
using System.Reflection;
using LookBusy;
using Xunit;

public class AllocationIdOverflowTests
{
	[Fact]
	public void AllocationId_NearIntMaxValue_HandlesOverflowCorrectly()
	{
		using var pool = new UnmanagedStringPool(1000);

		// Use reflection to set lastAllocationId to a value near int.MaxValue
		var lastAllocationIdField = typeof(UnmanagedStringPool)
			.GetField("lastAllocationId", BindingFlags.NonPublic | BindingFlags.Instance);

		Assert.NotNull(lastAllocationIdField);

		// Set to just before max value
		lastAllocationIdField.SetValue(pool, int.MaxValue - 1);

		// These allocations should handle the overflow gracefully
		var str1 = pool.Allocate("Test1"); // Should get MaxValue
		var str2 = pool.Allocate("Test2"); // Should get 1 (wrapped around)
		var str3 = pool.Allocate("Test3"); // Should get 2

		// Verify strings work correctly
		Assert.Equal("Test1", str1.ToString());
		Assert.Equal("Test2", str2.ToString());
		Assert.Equal("Test3", str3.ToString());

		// Verify allocation IDs are valid and different
		Assert.True(str1.AllocationId > 0);
		Assert.True(str2.AllocationId > 0);
		Assert.True(str3.AllocationId > 0);
		Assert.NotEqual(str1.AllocationId, str2.AllocationId);
		Assert.NotEqual(str2.AllocationId, str3.AllocationId);

		// Free should work correctly even after overflow
		str1.Free();
		str2.Free();
		str3.Free();
	}

	[Fact]
	public void AllocationId_AtIntMaxValue_WrapsToOne()
	{
		using var pool = new UnmanagedStringPool(1000);

		// Use reflection to set lastAllocationId to int.MaxValue
		var lastAllocationIdField = typeof(UnmanagedStringPool)
			.GetField("lastAllocationId", BindingFlags.NonPublic | BindingFlags.Instance);

		Assert.NotNull(lastAllocationIdField);
		lastAllocationIdField.SetValue(pool, int.MaxValue);

		// Next allocation should wrap to 1 (skipping 0 which is reserved for empty strings)
		var str = pool.Allocate("Test");

		Assert.Equal("Test", str.ToString());
		Assert.Equal(1, str.AllocationId);
	}
}
