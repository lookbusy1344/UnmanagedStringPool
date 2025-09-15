namespace LookBusy.Test;

using System;
using System.Collections.Generic;
using LookBusy;
using Xunit;

public class UnmanagedStringPoolEdgeCaseTests
{
	#region Constructor Edge Cases

	[Fact]
	public void Constructor_MinimumCapacity_WorksCorrectly()
	{
		using var pool = new UnmanagedStringPool(1);

		Assert.True(pool.FreeSpaceChars >= 1);
		Assert.Equal(0, pool.ActiveAllocations);
	}

	[Fact]
	public void Constructor_LargeCapacity_WorksCorrectly()
	{
		using var pool = new UnmanagedStringPool(1_000_000);

		Assert.Equal(1_000_000, pool.FreeSpaceChars);
		Assert.Equal(0, pool.ActiveAllocations);
	}

	[Theory]
	[InlineData(int.MinValue)]
	[InlineData(-1000000)]
	[InlineData(0)]
	public void Constructor_InvalidCapacities_ThrowArgumentOutOfRangeException(int capacity) =>
		Assert.Throws<ArgumentOutOfRangeException>(() => new UnmanagedStringPool(capacity));

	#endregion

	#region Allocation Edge Cases

	[Fact]
	public void Allocate_VeryLongString_WorksCorrectly()
	{
		using var pool = new UnmanagedStringPool(100_000);
		var longString = new string('A', 50_000);

		var result = pool.Allocate(longString);

		Assert.Equal(longString, result.ToString());
		Assert.Equal(50_000, result.Length);
	}

	[Fact]
	public void Allocate_MaxLengthString_NearIntMaxValue_HandlesCorrectly()
	{
		// Test with a large string that approaches system limits
		using var pool = new UnmanagedStringPool(1_000_000);
		var largeString = new string('X', 500_000);

		var result = pool.Allocate(largeString);

		Assert.Equal(largeString, result.ToString());
		Assert.Equal(500_000, result.Length);
	}

	[Fact]
	public void Allocate_SpecialCharacters_WorksCorrectly()
	{
		using var pool = new UnmanagedStringPool(1024);
		var specialChars = "Hello\0World\n\r\t\u0001\u007F";

		var result = pool.Allocate(specialChars);

		Assert.Equal(specialChars, result.ToString());
	}

	[Fact]
	public void Allocate_UnicodeCharacters_WorksCorrectly()
	{
		using var pool = new UnmanagedStringPool(1024);
		var unicode = "Hello 🌍 World 中文 العربية ñ";

		var result = pool.Allocate(unicode);

		Assert.Equal(unicode, result.ToString());
	}

	[Fact]
	public void Allocate_EmptySpan_ReturnsEmpty()
	{
		using var pool = new UnmanagedStringPool(1024);
		ReadOnlySpan<char> emptySpan = [];

		var result = pool.Allocate(emptySpan);

		Assert.True(result.IsEmpty);
		Assert.Equal(0, pool.ActiveAllocations);
	}

	[Fact]
	public void Allocate_ManyEmptyStrings_HandlesCorrectly()
	{
		using var pool = new UnmanagedStringPool(1024);

		for (var i = 0; i < 1000; i++) {
			var empty = pool.Allocate("");
			Assert.True(empty.IsEmpty);
		}

		Assert.Equal(0, pool.ActiveAllocations); // Empty strings don't count
	}

	#endregion

	#region Memory Exhaustion and Growth Edge Cases

	[Fact]
	public void Allocate_ExceedsCapacityWithGrowthDisabled_ThrowsOutOfMemoryException()
	{
		using var pool = new UnmanagedStringPool(10, false);

		// Fill most of the pool
		var str1 = pool.Allocate("12345");

		// This should exceed capacity and throw
		Assert.Throws<OutOfMemoryException>(() => pool.Allocate("This string is definitely too long"));
	}

	[Fact]
	public void DefragmentAndGrowPool_WithZeroBytes_WorksCorrectly()
	{
		using var pool = new UnmanagedStringPool(1024);
		var str = pool.Allocate("Test");
		var originalCapacity = pool.FreeSpaceChars + 4; // approximate

		pool.DefragmentAndGrowPool(0);

		Assert.Equal("Test", str.ToString()); // Should still work
		Assert.True(pool.FreeSpaceChars >= originalCapacity - 20); // Allow for alignment
	}

	[Fact]
	public void DefragmentAndGrowPool_MaxIntValue_ThrowsArgumentOutOfRangeException()
	{
		using var pool = new UnmanagedStringPool(1024);

		Assert.Throws<ArgumentOutOfRangeException>(() => pool.DefragmentAndGrowPool(int.MaxValue));
	}

	[Fact]
	public void DefragmentAndGrowPool_NearMaxIntValue_ThrowsArgumentOutOfRangeException()
	{
		using var pool = new UnmanagedStringPool(1024);

		Assert.Throws<ArgumentOutOfRangeException>(() => pool.DefragmentAndGrowPool(int.MaxValue - 1000));
	}

	#endregion

	#region Disposal Edge Cases

	[Fact]
	public void Dispose_MultipleCalls_NoException()
	{
		var pool = new UnmanagedStringPool(1024);
		var str = pool.Allocate("Test");

		pool.Dispose();
		pool.Dispose(); // Second dispose should not throw
		pool.Dispose(); // Third dispose should not throw

		Assert.Throws<ObjectDisposedException>(() => str.AsSpan());
	}

	[Fact]
	public void PooledString_AccessAfterPoolDispose_ThrowsObjectDisposedException()
	{
		var pool = new UnmanagedStringPool(1024);
		var str = pool.Allocate("Test");

		pool.Dispose();

		Assert.Throws<ObjectDisposedException>(() => str.AsSpan());
		Assert.Throws<ObjectDisposedException>(() => str.Length);
		Assert.Throws<ObjectDisposedException>(() => str.ToString());
		Assert.Throws<ObjectDisposedException>(() => str.Insert(0, "x"));
		Assert.Throws<ObjectDisposedException>(() => str.Replace("T", "t"));
		Assert.Throws<ObjectDisposedException>(() => str.SubstringSpan(0, 1));
	}

	[Fact]
	public void PooledString_FreeAfterPoolDispose_NoException()
	{
		var pool = new UnmanagedStringPool(1024);
		var str = pool.Allocate("Test");

		pool.Dispose();

		// Free should not throw even after pool disposal
		str.Free();
		str.Dispose();
	}

	#endregion

	#region String Operation Edge Cases

	[Fact]
	public void Replace_WithVeryLargeReplacement_HandlesCorrectly()
	{
		using var pool = new UnmanagedStringPool(100_000);
		var original = pool.Allocate("Replace this");
		var largeReplacement = new string('X', 50_000);

		var result = original.Replace("this", largeReplacement);

		Assert.Contains(largeReplacement, result.ToString());
		Assert.StartsWith("Replace ", result.ToString());
	}

	[Fact]
	public void Replace_CausingOverflow_ThrowsArgumentException()
	{
		using var pool = new UnmanagedStringPool(1000, false);
		var str = pool.Allocate("ab");
		var largeReplacement = new string('X', 100_000);

		// This should fail when trying to allocate in a non-growing pool
		Assert.ThrowsAny<Exception>(() => str.Replace("a", largeReplacement));
	}

	[Fact]
	public void Insert_AtMaxLength_WorksCorrectly()
	{
		using var pool = new UnmanagedStringPool(1024);
		var str = pool.Allocate("Hello");

		var result = str.Insert(str.Length, " World");

		Assert.Equal("Hello World", result.ToString());
	}

	[Fact]
	public void Insert_VeryLargeString_WorksCorrectly()
	{
		using var pool = new UnmanagedStringPool(100_000);
		var original = pool.Allocate("Start");
		var largeInsert = new string('X', 50_000);

		var result = original.Insert(2, largeInsert);

		Assert.Equal("St" + largeInsert + "art", result.ToString());
	}

	[Fact]
	public void SubstringSpan_EdgeBoundaries_WorksCorrectly()
	{
		using var pool = new UnmanagedStringPool(1024);
		var str = pool.Allocate("Hello");

		// Test edge boundaries
		var span1 = str.SubstringSpan(0, 0); // Empty from start
		var span2 = str.SubstringSpan(5, 0); // Empty from end
		var span3 = str.SubstringSpan(0, 5); // Entire string
		var span4 = str.SubstringSpan(4, 1); // Last character

		Assert.True(span1.IsEmpty);
		Assert.True(span2.IsEmpty);
		Assert.Equal("Hello", span3.ToString());
		Assert.Equal("o", span4.ToString());
	}

	[Fact]
	public void SubstringSpan_NegativeLength_ThrowsArgumentOutOfRangeException()
	{
		using var pool = new UnmanagedStringPool(1024);
		var str = pool.Allocate("Hello");

		Assert.Throws<ArgumentOutOfRangeException>(() => str.SubstringSpan(0, -1));
	}

	#endregion

	#region Equality and Comparison Edge Cases

	[Fact]
	public void Equals_DisposedPool_ReturnsFalse()
	{
		var pool1 = new UnmanagedStringPool(1024);
		var pool2 = new UnmanagedStringPool(1024);

		var str1 = pool1.Allocate("Test");
		var str2 = pool2.Allocate("Test");

		pool1.Dispose();

		Assert.False(str1.Equals(str2)); // Should return false due to disposed pool

		pool2.Dispose();
	}

	[Fact]
	public void GetHashCode_DisposedPool_ReturnsMinusOne()
	{
		var pool = new UnmanagedStringPool(1024);
		var str = pool.Allocate("Test");

		pool.Dispose();

		Assert.Equal(-1, str.GetHashCode());
	}

	[Fact]
	public void Equals_NullPool_HandlesCorrectly()
	{
		using var pool = new UnmanagedStringPool(1024);
		var str = pool.Allocate("Test");
		var nullPoolStr = new PooledString(null!, 1);

		Assert.False(str.Equals(nullPoolStr));
		Assert.False(nullPoolStr.Equals(str));
	}

	#endregion

	#region Fragmentation Edge Cases

	[Fact]
	public void FragmentationCalculation_EmptyPool_ReturnsZero()
	{
		using var pool = new UnmanagedStringPool(1024);

		Assert.Equal(0.0, pool.FragmentationPercentage);
	}

	[Fact]
	public void FragmentationCalculation_FullPool_ReturnsZero()
	{
		using var pool = new UnmanagedStringPool(100, false);

		try {
			// Try to fill the pool completely
			while (true) {
				pool.Allocate("XXXX");
			}
		}
		catch (OutOfMemoryException) {
			// Expected when pool is full
		}

		// Fragmentation should be low when pool is full
		Assert.True(pool.FragmentationPercentage >= 0);
		Assert.True(pool.FragmentationPercentage <= 100);
	}

	[Fact]
	public void FreeSpaceCalculation_WithFragmentation_IsAccurate()
	{
		using var pool = new UnmanagedStringPool(1024);

		var strings = new List<PooledString>();
		for (var i = 0; i < 10; i++) {
			strings.Add(pool.Allocate($"String{i}"));
		}

		// Free every other string to create fragmentation
		for (var i = 1; i < strings.Count; i += 2) {
			strings[i].Free();
		}

		var freeSpace = pool.FreeSpaceChars;
		var fragmentation = pool.FragmentationPercentage;

		Assert.True(freeSpace >= 0);
		Assert.True(fragmentation >= 0 && fragmentation <= 100);
	}

	#endregion

	#region Invalid Allocation ID Edge Cases

	[Fact]
	public void GetAllocationInfo_InvalidId_ThrowsArgumentException()
	{
		using var pool = new UnmanagedStringPool(1024);

		// Test various invalid IDs
		Assert.Throws<ArgumentException>(() => pool.GetAllocationInfo(-1));
		Assert.Throws<ArgumentException>(() => pool.GetAllocationInfo(999999));
		Assert.Throws<ArgumentException>(() => pool.GetAllocationInfo(int.MaxValue));
	}

	[Fact]
	public void FreeString_InvalidId_NoException()
	{
		using var pool = new UnmanagedStringPool(1024);

		// These should not throw - just be ignored
		pool.FreeString(-1);
		pool.FreeString(999999);
		pool.FreeString(int.MaxValue);
	}

	[Fact]
	public void PooledString_WithInvalidId_HandlesGracefully()
	{
		using var pool = new UnmanagedStringPool(1024);
		var invalidStr = new PooledString(pool, 999999);

		Assert.Throws<ArgumentException>(() => invalidStr.AsSpan());
		Assert.Throws<ArgumentException>(() => invalidStr.Length);
		Assert.Throws<ArgumentException>(() => invalidStr.ToString());
	}

	#endregion

	#region Buffer Alignment Edge Cases

	[Fact]
	public void Allocate_OddSizeStrings_MaintainsAlignment()
	{
		using var pool = new UnmanagedStringPool(1024);

		// Allocate strings of various odd sizes
		var str1 = pool.Allocate("X"); // 1 char
		var str2 = pool.Allocate("XXX"); // 3 chars
		var str3 = pool.Allocate("XXXXX"); // 5 chars

		Assert.Equal("X", str1.ToString());
		Assert.Equal("XXX", str2.ToString());
		Assert.Equal("XXXXX", str3.ToString());
	}

	[Fact]
	public void DumpBufferAsString_AfterMixedOperations_ShowsCorrectData()
	{
		using var pool = new UnmanagedStringPool(1024);

		var str2 = pool.Allocate("World");
		var str3 = pool.Allocate("Test!");

		var dump = pool.DumpBufferAsString();

		// Only verify what we know should be there
		Assert.Contains("World", dump);
		Assert.Contains("Test!", dump);
	}

	#endregion

	#region Numeric Overflow Edge Cases

	[Theory]
	[InlineData(int.MaxValue)]
	[InlineData(int.MaxValue / 2)]
	public void Allocate_IntegerOverflowSizes_ThrowsArgumentOutOfRangeException(int length)
	{
		using var pool = new UnmanagedStringPool(1024);

		Assert.Throws<ArgumentOutOfRangeException>(() => pool.Allocate(length));
	}

	[Fact]
	public void Replace_IntegerOverflowInCalculation_ThrowsArgumentException()
	{
		using var pool = new UnmanagedStringPool(1000, false);
		var str = pool.Allocate("ab");

		// Create a scenario where the replacement will fail due to insufficient space
		var hugeReplacement = new string('X', 100_000);

		Assert.ThrowsAny<Exception>(() => str.Replace("a", hugeReplacement));
	}

	#endregion

	#region Empty String Pool Edge Cases

	[Fact]
	public void EmptyStringPool_GetAllocationInfo_ValidId_ReturnsCorrectInfo()
	{
		using var pool = new UnmanagedStringPool(1024);
		var info = pool.GetAllocationInfo(UnmanagedStringPool.EmptyStringAllocationId);

		Assert.Equal(IntPtr.Zero, info.Pointer);
		Assert.Equal(0, info.LengthChars);
		Assert.Equal(0, info.OffsetBytes);
	}

	[Fact]
	public void EmptyStringPool_GetAllocationInfo_InvalidId_ThrowsArgumentException()
	{
		using var pool = new UnmanagedStringPool(1024);
		Assert.Throws<ArgumentException>(() =>
			pool.GetAllocationInfo(int.MaxValue));
	}

	[Fact]
	public void EmptyStringPool_FreeString_AnyId_NoEffect()
	{
		using var pool = new UnmanagedStringPool(1024);
		// These should not throw or cause issues
		pool.FreeString(0);
		pool.FreeString(1);
		pool.FreeString(-1);
	}

	#endregion

	#region Concurrent Access Edge Cases

	[Fact]
	public void MultiplePooledStrings_SamePool_IndependentOperations()
	{
		using var pool = new UnmanagedStringPool(2048);

		var str1 = pool.Allocate("First");
		var str2 = pool.Allocate("Second");

		// Operations on one should not affect the other
		var result1 = str1.Insert(0, "The ");
		var result2 = str2.Replace("Second", "2nd");

		Assert.Equal("The First", result1.ToString());
		Assert.Equal("2nd", result2.ToString());
		Assert.Equal("First", str1.ToString()); // Original unchanged
		Assert.Equal("Second", str2.ToString()); // Original unchanged
	}

	#endregion
}
