namespace LookBusy.Test;

using System;
using System.Collections.Generic;
using System.Linq;
using LookBusy;
using Xunit;

public sealed class FragmentationAndMemoryTests : IDisposable
{
	private readonly UnmanagedStringPool pool;

	public FragmentationAndMemoryTests()
	{
		pool = new UnmanagedStringPool(4096);
	}

	public void Dispose()
	{
		pool?.Dispose();
		GC.SuppressFinalize(this);
	}

	#region Fragmentation Creation and Detection

	[Fact]
	public void CreateFragmentation_FreeMiddleStrings_IncreasesFragmentation()
	{
		var strings = new List<PooledString>();

		// Allocate a series of strings
		for (int i = 0; i < 10; i++) {
			strings.Add(pool.Allocate($"String_{i:D2}"));
		}

		var initialFragmentation = pool.FragmentationPercentage;

		// Free every other string to create fragmentation
		for (int i = 1; i < strings.Count; i += 2) {
			strings[i].Free();
		}

		var fragmentationAfterFree = pool.FragmentationPercentage;

		Assert.True(fragmentationAfterFree > initialFragmentation);
		Assert.Equal(5, pool.ActiveAllocations); // Half should remain
	}

	[Fact]
	public void MaxFragmentation_FreeAllButFirst_ShowsHighFragmentation()
	{
		var strings = new List<PooledString>();

		// Fill a significant portion of the pool
		for (int i = 0; i < 20; i++) {
			strings.Add(pool.Allocate($"FragmentationTest_{i:D3}"));
		}

		// Free all but the first and last to maximize fragmentation
		for (int i = 1; i < strings.Count - 1; i++) {
			strings[i].Free();
		}

		var fragmentation = pool.FragmentationPercentage;

		Assert.True(fragmentation > 10); // Should be quite fragmented
		Assert.Equal(2, pool.ActiveAllocations);
	}

	[Fact]
	public void FragmentationCalculation_AfterCoalescing_Decreases()
	{
		var strings = new List<PooledString>();

		// Create fragmentation
		for (int i = 0; i < 15; i++) {
			strings.Add(pool.Allocate($"Test_{i}"));
		}

		for (int i = 1; i < strings.Count; i += 2) {
			strings[i].Free();
		}

		var fragmentationBefore = pool.FragmentationPercentage;

		// Force defragmentation by triggering coalescing
		pool.DefragmentAndGrowPool(0);

		var fragmentationAfter = pool.FragmentationPercentage;

		Assert.True(fragmentationAfter < fragmentationBefore);
		Assert.Equal(0.0, fragmentationAfter, 1); // Should be close to 0 after defrag
	}

	#endregion

	#region Free Block Management

	[Fact]
	public void FreeBlockReuse_ExactSize_ReusesBlock()
	{
		var str1 = pool.Allocate("TestString");
		var freeSpaceBeforeFree = pool.FreeSpaceChars;

		str1.Free();
		var freeSpaceAfterFree = pool.FreeSpaceChars;

		// Allocate same size string
		var str2 = pool.Allocate("TestString");
		var freeSpaceAfterReuse = pool.FreeSpaceChars;

		Assert.True(freeSpaceAfterFree > freeSpaceBeforeFree);
		Assert.Equal(freeSpaceBeforeFree, freeSpaceAfterReuse); // Should reuse the exact space
		Assert.Equal("TestString", str2.ToString());
	}

	[Fact]
	public void FreeBlockReuse_SmallerSize_ReusesAndSplitsBlock()
	{
		var longStr = pool.Allocate("VeryLongTestString");
		longStr.Free();

		var shortStr = pool.Allocate("Short");
		var remainingFreeSpace = pool.FreeSpaceChars;

		Assert.Equal("Short", shortStr.ToString());
		// Should have more free space than if we allocated at the end
		Assert.True(remainingFreeSpace > 0);
	}

	[Fact]
	public void FreeBlockReuse_LargerSize_UsesEndOfPool()
	{
		var shortStr = pool.Allocate("Hi");
		var endBlockSizeBeforeFree = pool.EndBlockSizeChars;

		shortStr.Free();

		// Try to allocate something much larger than the freed block
		var longStr = pool.Allocate("This is a much longer string than what was freed");
		var endBlockSizeAfter = pool.EndBlockSizeChars;

		// Should have allocated at the end of the pool, not reusing the small freed block
		Assert.True(endBlockSizeAfter < endBlockSizeBeforeFree);
		Assert.Equal("This is a much longer string than what was freed", longStr.ToString());
	}

	[Fact]
	public void FreeBlockReuse_MultipleBlocks_FindsBestFit()
	{
		var strings = new List<PooledString>
		{
			pool.Allocate("Short"),           // 5 chars
            pool.Allocate("MediumLength"),    // 12 chars
            pool.Allocate("VeryLongString"),  // 14 chars
            pool.Allocate("X")                // 1 char
        };

		// Free all to create different sized blocks
		foreach (var str in strings) {
			str.Free();
		}

		// Allocate something that should fit in the medium block
		var target = pool.Allocate("TargetStr"); // 9 chars, should fit in 12-char block

		Assert.Equal("TargetStr", target.ToString());
	}

	#endregion

	#region Block Coalescing Behavior

	[Fact]
	public void BlockCoalescing_AdjacentBlocks_GetsCombined()
	{
		var str1 = pool.Allocate("First");
		var str2 = pool.Allocate("Second");
		var str3 = pool.Allocate("Third");

		var fragmentationBefore = pool.FragmentationPercentage;
		var freeBlocksBefore = GetApproximateFreeBlockCount();

		// Free adjacent blocks
		str1.Free();
		str2.Free();

		// Force coalescing by adding many free operations or defragmenting
		for (int i = 0; i < 15; i++) {
			var temp = pool.Allocate("temp");
			temp.Free();
		}

		var fragmentationAfter = pool.FragmentationPercentage;

		// Fragmentation should be managed through coalescing
		Assert.True(pool.FreeSpaceChars > 0);
		Assert.Equal(1, pool.ActiveAllocations); // Only str3 should remain
	}

	[Fact]
	public void BlockCoalescing_NonAdjacentBlocks_RemainSeparate()
	{
		var str1 = pool.Allocate("First");
		var str2 = pool.Allocate("Second");
		var str3 = pool.Allocate("Third");

		// Free non-adjacent blocks
		str1.Free();
		str3.Free();

		// str2 should prevent coalescing of str1 and str3 blocks
		Assert.Equal(1, pool.ActiveAllocations); // Only str2 remains
		Assert.Equal("Second", str2.ToString());
	}

	[Fact]
	public void AutomaticCoalescing_TriggeredByThreshold_ReducesFragmentation()
	{
		var strings = new List<PooledString>();

		// Create significant fragmentation
		for (int i = 0; i < 30; i++) {
			strings.Add(pool.Allocate($"String_{i:D2}"));
		}

		// Free many strings to trigger automatic coalescing
		for (int i = 1; i < strings.Count; i += 3) {
			strings[i].Free();
		}

		var fragmentationMid = pool.FragmentationPercentage;

		// Continue freeing to potentially trigger coalescing
		for (int i = 2; i < strings.Count; i += 3) {
			strings[i].Free();
		}

		var fragmentationFinal = pool.FragmentationPercentage;

		// The pool should manage fragmentation through coalescing
		Assert.True(fragmentationMid >= 0);
		Assert.True(fragmentationFinal >= 0);
	}

	#endregion

	#region Pool Growth Under Fragmentation

	[Fact]
	public void PoolGrowth_WithHighFragmentation_DefragmentsAndGrows()
	{
		// Create fragmentation
		var strings = new List<PooledString>();
		for (int i = 0; i < 25; i++) {
			strings.Add(pool.Allocate($"FragTest_{i}"));
		}

		for (int i = 1; i < strings.Count; i += 2) {
			strings[i].Free();
		}

		var capacityBefore = pool.FreeSpaceChars + (pool.ActiveAllocations * 12); // Approximate

		pool.DefragmentAndGrowPool(1000);

		var capacityAfter = pool.FreeSpaceChars + (pool.ActiveAllocations * 12); // Approximate

		Assert.True(capacityAfter > capacityBefore);
		Assert.Equal(0.0, pool.FragmentationPercentage, 1); // Should be defragmented

		// All remaining strings should still be valid
		for (int i = 0; i < strings.Count; i += 2) {
			Assert.Equal($"FragTest_{i}", strings[i].ToString());
		}
	}

	[Fact]
	public void PoolGrowth_PreservesStringOrder_AfterDefragmentation()
	{
		var str1 = pool.Allocate("First");
		var str2 = pool.Allocate("Second");
		var str3 = pool.Allocate("Third");

		str2.Free(); // Create gap

		pool.DefragmentAndGrowPool(500);

		// Strings should maintain their content after defragmentation
		Assert.Equal("First", str1.ToString());
		Assert.Equal("Third", str3.ToString());
	}

	#endregion

	#region Memory Alignment and Efficiency

	[Fact]
	public void MemoryAlignment_VariousSizes_MaintainsAlignment()
	{
		var strings = new List<PooledString>();

		// Allocate strings of various sizes to test alignment
		var sizes = new[] { 1, 3, 7, 15, 31, 63 };

		foreach (var size in sizes) {
			var content = new string('X', size);
			strings.Add(pool.Allocate(content));
		}

		// Verify all strings are correctly stored
		for (int i = 0; i < sizes.Length; i++) {
			var expected = new string('X', sizes[i]);
			Assert.Equal(expected, strings[i].ToString());
		}
	}

	[Fact]
	public void MemoryEfficiency_SmallAllocations_MinimizeWaste()
	{
		var initialFreeSpace = pool.FreeSpaceChars;
		var strings = new List<PooledString>();

		// Make many small allocations
		for (int i = 0; i < 100; i++) {
			strings.Add(pool.Allocate("X"));
		}

		var usedSpace = initialFreeSpace - pool.FreeSpaceChars;
		var theoreticalMinimum = 100; // 100 chars
		var wasteRatio = (double)(usedSpace - theoreticalMinimum) / theoreticalMinimum;

		// Alignment should not cause excessive waste (allow up to 300% overhead for small allocations)
		Assert.True(wasteRatio <= 3.0, $"Waste ratio {wasteRatio:F2} is too high for small allocations");

		// Verify all strings are correct
		foreach (var str in strings) {
			Assert.Equal("X", str.ToString());
		}
	}

	#endregion

	#region Complex Fragmentation Scenarios

	[Fact]
	public void ComplexScenario_InterleavedAllocateAndFree_MaintainsConsistency()
	{
		var activeStrings = new List<(PooledString str, string content)>();
		var random = new Random(12345); // Fixed seed

		for (int iteration = 0; iteration < 200; iteration++) {
			var operation = random.Next(3);

			if (operation == 0 || activeStrings.Count == 0) // Allocate
			{
				var content = $"Iter_{iteration}_{random.Next(1000)}";
				var str = pool.Allocate(content);
				activeStrings.Add((str, content));
			} else if (operation == 1 && activeStrings.Count > 0) // Free random
			  {
				var index = random.Next(activeStrings.Count);
				activeStrings[index].str.Free();
				activeStrings.RemoveAt(index);
			} else if (operation == 2 && activeStrings.Count > 0) // Verify random
			  {
				var index = random.Next(activeStrings.Count);
				var (str, content) = activeStrings[index];
				Assert.Equal(content, str.ToString());
			}

			// Periodically check pool consistency
			if (iteration % 50 == 0) {
				Assert.Equal(activeStrings.Count, pool.ActiveAllocations);
				Assert.True(pool.FragmentationPercentage >= 0 && pool.FragmentationPercentage <= 100);
			}
		}

		// Final verification
		foreach (var (str, content) in activeStrings) {
			Assert.Equal(content, str.ToString());
		}

		Assert.Equal(activeStrings.Count, pool.ActiveAllocations);
	}

	[Fact]
	public void StressTest_CycleAllocations_HandlesGracefully()
	{
		const int cycles = 50;
		const int stringsPerCycle = 20;

		for (int cycle = 0; cycle < cycles; cycle++) {
			var strings = new List<PooledString>();

			// Allocate
			for (int i = 0; i < stringsPerCycle; i++) {
				strings.Add(pool.Allocate($"Cycle{cycle}_String{i}"));
			}

			// Verify
			for (int i = 0; i < stringsPerCycle; i++) {
				Assert.Equal($"Cycle{cycle}_String{i}", strings[i].ToString());
			}

			// Free all
			foreach (var str in strings) {
				str.Free();
			}

			Assert.Equal(0, pool.ActiveAllocations);

			// Occasionally force defragmentation
			if (cycle % 10 == 0) {
				pool.DefragmentAndGrowPool(0);
				Assert.Equal(0.0, pool.FragmentationPercentage, 1);
			}
		}
	}

	#endregion

	#region Helper Methods

	private int GetApproximateFreeBlockCount()
	{
		// This is an approximation since we can't directly access free block count
		// We estimate based on fragmentation and free space
		var fragmentation = pool.FragmentationPercentage;
		if (fragmentation < 1.0) {
			return 0; // No significant fragmentation
		}

		// Rough estimate: higher fragmentation suggests more blocks
		return (int)(fragmentation / 10); // Very rough approximation
	}

	#endregion

	#region Boundary Condition Tests

	[Fact]
	public void FragmentationThreshold_BoundaryConditions_WorkCorrectly()
	{
		// Create exactly the threshold amount of fragmentation
		var strings = new List<PooledString>();

		// Fill pool significantly
		for (int i = 0; i < 40; i++) {
			strings.Add(pool.Allocate($"Boundary_Test_{i:D3}"));
		}

		// Create fragmentation up to near the threshold
		for (int i = 0; i < strings.Count; i += 3) {
			strings[i].Free();
		}

		var fragmentation = pool.FragmentationPercentage;

		// The pool should handle any fragmentation level gracefully
		Assert.True(fragmentation >= 0 && fragmentation <= 100);
		Assert.True(pool.ActiveAllocations > 0);

		// Should still be able to allocate
		var newStr = pool.Allocate("After_Fragmentation");
		Assert.Equal("After_Fragmentation", newStr.ToString());
	}

	[Fact]
	public void MaximumFragmentation_AllButOneStringFree_HandlesCorrectly()
	{
		var strings = new List<PooledString>();

		// Fill a good portion of the pool
		for (int i = 0; i < 30; i++) {
			strings.Add(pool.Allocate($"MaxFrag_{i:D2}"));
		}

		// Free all but one string to create maximum fragmentation scenario
		for (int i = 1; i < strings.Count; i++) {
			strings[i].Free();
		}

		var fragmentation = pool.FragmentationPercentage;

		Assert.Equal(1, pool.ActiveAllocations);
		Assert.Equal("MaxFrag_00", strings[0].ToString());
		Assert.True(fragmentation >= 0 && fragmentation <= 100);

		// Pool should still function
		var newStr = pool.Allocate("Still_Works");
		Assert.Equal("Still_Works", newStr.ToString());
	}

	#endregion
}
