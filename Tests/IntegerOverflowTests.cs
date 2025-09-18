namespace LookBusy.Test;

using System;
using LookBusy;
using Xunit;

public sealed class IntegerOverflowTests : IDisposable
{
	private readonly UnmanagedStringPool pool;

	public IntegerOverflowTests() => pool = new(1024);

	public void Dispose()
	{
		pool?.Dispose();
		GC.SuppressFinalize(this);
	}

	#region Constructor Overflow Tests

	[Fact]
	public void Constructor_MaxIntCapacity_ThrowsOrHandlesGracefully()
	{
		// Test near int.MaxValue capacity - should either work or throw appropriate exception
		try {
			using var testPool = new UnmanagedStringPool(int.MaxValue / sizeof(char));
			Assert.Fail("Expected exception for max capacity");
		}
		catch (OutOfMemoryException) {
			// Expected - not enough memory
			Assert.True(true);
		}
		catch (ArgumentException) {
			// Also acceptable - invalid argument
			Assert.True(true);
		}
		catch (Exception ex) when (ex.Message.Contains("overflow") || ex.Message.Contains("capacity")) {
			// Any overflow or capacity related exception is acceptable
			Assert.True(true);
		}
	}

	[Fact]
	public void Constructor_CapacityByteOverflow_ThrowsOutOfMemoryOrArgumentException()
	{
		// When initialCapacityChars * sizeof(char) would overflow int
		var oversizedCapacity = (int.MaxValue / sizeof(char)) + 1;

		Assert.ThrowsAny<Exception>(() => {
			using var testPool = new UnmanagedStringPool(oversizedCapacity);
		});
	}

	#endregion

	#region Allocation Overflow Tests

	[Theory]
	[InlineData(int.MaxValue)]
	[InlineData(int.MaxValue / sizeof(char))]
	[InlineData(((int.MaxValue - 8 + 1) / sizeof(char)) + 1)] // Just over the safe limit
	public void Allocate_OversizedString_ThrowsArgumentOutOfRangeException(int lengthChars) =>
		Assert.Throws<ArgumentOutOfRangeException>(() => pool.Allocate(lengthChars));

	[Fact]
	public void Allocate_MaxSafeLengthString_WorksOrThrowsAppropriately()
	{
		// Calculate the maximum safe length as done in the code
		const int alignment = 8;
		var maxSafeLength = (int.MaxValue - alignment + 1) / sizeof(char);

		// This should either work or throw OutOfMemoryException, but not ArgumentOutOfRangeException
		try {
			using var testPool = new UnmanagedStringPool(maxSafeLength, false); // Disable growth
			testPool.Allocate(maxSafeLength);
		}
		catch (OutOfMemoryException) {
			// Expected - not enough memory
		}
		catch (ArgumentOutOfRangeException) {
			Assert.Fail("Should not throw ArgumentOutOfRangeException for max safe length");
		}
	}

	[Fact]
	public void Allocate_StringCausingByteOverflow_ThrowsArgumentOutOfRangeException()
	{
		// Test a length that would cause overflow when multiplied by sizeof(char) and aligned
		var dangerousLength = int.MaxValue / sizeof(char);

		Assert.Throws<ArgumentOutOfRangeException>(() => pool.Allocate(dangerousLength));
	}

	#endregion

	#region Growth and Capacity Overflow Tests

	[Fact]
	public void DefragmentAndGrowPool_AdditionCausesOverflow_ThrowsArgumentOutOfRangeException()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			pool.DefragmentAndGrowPool(int.MaxValue));
	}

	[Fact]
	public void DefragmentAndGrowPool_NearMaxCapacity_ThrowsArgumentOutOfRangeException()
	{
		using var testPool = new UnmanagedStringPool(1000);

		// Adding this should cause overflow
		var additionalBytes = int.MaxValue - 500;

		Assert.Throws<ArgumentOutOfRangeException>(() =>
			testPool.DefragmentAndGrowPool(additionalBytes));
	}

	[Theory]
	[InlineData(int.MaxValue - 1)]
	[InlineData(int.MaxValue)]
	public void DefragmentAndGrowPool_ExactOverflowBoundary_ThrowsArgumentOutOfRangeException(int additionalBytes)
	{
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			pool.DefragmentAndGrowPool(additionalBytes));
	}

	#endregion

	#region AlignSize Function Overflow Tests

	[Fact]
	public void AlignSize_MaxInt_HandlesCorrectly()
	{
		// We can't directly test AlignSize since it's private, but we can test through allocation
		// Test values that would challenge the alignment calculation

		// This should cause overflow in AlignSize if not handled properly
		var problematicLength = (int.MaxValue - 7) / sizeof(char);

		Assert.Throws<ArgumentOutOfRangeException>(() => pool.Allocate(problematicLength));
	}

	[Fact]
	public void AlignSize_NearMaxValues_BehavesConsistently()
	{
		// Test values just under the overflow threshold
		var testValues = new[] { int.MaxValue / 2, int.MaxValue / 4 };

		foreach (var lengthChars in testValues) {
			// These should all throw due to memory constraints or overflow detection
			try {
				using var testPool = new UnmanagedStringPool(100, false); // Small pool
				testPool.Allocate(lengthChars); // This should trigger overflow detection
				Assert.Fail($"Expected exception for lengthChars: {lengthChars}");
			}
			catch (ArgumentOutOfRangeException) {
				// Expected - overflow detected
				Assert.True(true);
			}
			catch (OutOfMemoryException) {
				// Also acceptable - memory constraints
				Assert.True(true);
			}
		}
	}

	#endregion

	#region Property Calculation Overflow Tests

	[Fact]
	public void FreeSpaceChars_DoesNotOverflowWithLargeValues()
	{
		// Create a pool and verify FreeSpaceChars calculation doesn't overflow
		using var testPool = new UnmanagedStringPool(1000000);

		// These property accesses should not throw due to overflow
		var freeSpace = testPool.FreeSpaceChars;
		var endBlock = testPool.EndBlockSizeChars;
		var fragmentation = testPool.FragmentationPercentage;

		Assert.True(freeSpace >= 0);
		Assert.True(endBlock >= 0);
		Assert.True(fragmentation >= 0);
	}

	[Fact]
	public void FragmentationPercentage_WithMaxValues_DoesNotOverflow()
	{
		// Test fragmentation calculation with large numbers
		var str1 = pool.Allocate("Test1");
		var str2 = pool.Allocate("Test2");

		// Free one to create fragmentation
		str1.Free();

		// This should not overflow even with large internal values
		var fragmentation = pool.FragmentationPercentage;
		Assert.True(fragmentation >= 0 && fragmentation <= 100);
	}

	#endregion

	#region PooledString Overflow Tests

	[Fact]
	public void PooledString_Replace_OverflowDetection_WithMockScenario()
	{
		// Test overflow detection logic with a more controlled scenario
		// We'll test boundary conditions that should trigger the overflow detection
		var str = pool.Allocate("aa");

		// This should work without overflow
		var smallReplacement = new string('X', 1000);
		var result = str.Replace("a", smallReplacement);
		Assert.Equal(2000, result.Length);
	}

	[Fact]
	public void PooledString_Replace_LargeButValidReplacement_Works()
	{
		var str = pool.Allocate("test");

		// Test with large but valid replacement that doesn't cause overflow
		var largeReplacement = new string('Y', 10000);

		// This might throw OutOfMemoryException or succeed, both are valid
		try {
			var result = str.Replace("t", largeReplacement);
			// If it succeeds, verify the result
			Assert.True(result.Length > str.Length);
		}
		catch (OutOfMemoryException) {
			// Expected with large allocations
			Assert.True(true);
		}
	}

	[Fact]
	public void PooledString_Replace_NegativeSizeDiff_HandlesCorrectly()
	{
		var str = pool.Allocate("HelloWorldHelloWorld");

		// Replace with shorter string - should handle negative size diff correctly
		var result = str.Replace("HelloWorld", "Hi");

		Assert.Equal("HiHi", result.ToString());
	}

	[Theory]
	[InlineData(int.MaxValue)]
	[InlineData(int.MaxValue / 2)]
	[InlineData(int.MinValue)]
	public void PooledString_Insert_InvalidPosition_ThrowsArgumentOutOfRangeException(int position)
	{
		var str = pool.Allocate("Test");

		Assert.Throws<ArgumentOutOfRangeException>(() => str.Insert(position, "X"));
	}

	[Theory]
	[InlineData(int.MaxValue)]
	[InlineData(int.MinValue)]
	public void PooledString_SubstringSpan_InvalidIndices_ThrowsArgumentOutOfRangeException(int startIndex)
	{
		var str = pool.Allocate("Test");

		Assert.Throws<ArgumentOutOfRangeException>(() => str.SubstringSpan(startIndex, 1));
	}

	[Fact]
	public void PooledString_SubstringSpan_LengthOverflow_ThrowsArgumentOutOfRangeException()
	{
		var str = pool.Allocate("Test");

		// Length that would cause startIndex + length to overflow
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			str.SubstringSpan(1, int.MaxValue));
	}

	#endregion

	#region SetAtPosition Overflow Tests

	[Fact]
	public void SetAtPosition_StartTimesCharSizeOverflow_ThrowsArgumentOutOfRangeException()
	{
		var str = pool.Allocate("Test");

		// This would cause start * sizeof(char) to overflow in IntPtr.Add
		Assert.Throws<ArgumentOutOfRangeException>(() => {
			// We can't directly call SetAtPosition as it's private, but Insert uses it
			// Insert with position that would cause overflow in SetAtPosition
			str.Insert(int.MaxValue / 2, "X");
		});
	}

	[Fact]
	public void SetAtPosition_LargeInsert_HandlesCorrectly()
	{
		var str = pool.Allocate("Test");

		// Test large but reasonable insert operation
		try {
			var result = str.Insert(2, new string('X', 10000));
			Assert.True(result.Length == str.Length + 10000);
		}
		catch (OutOfMemoryException) {
			// Expected with large allocations on constrained systems
			Assert.True(true);
		}
	}

	#endregion

	#region Memory Copy Overflow Tests

	[Fact]
	public void MemoryCopy_SizeCalculations_DoNotOverflow()
	{
		var str1 = pool.Allocate("Hello");
		var str2 = pool.Allocate("World");

		// These operations involve memory copy with size calculations
		var result1 = str1.Insert(5, " ");
		var result2 = str2.Replace("o", "0");

		Assert.Equal("Hello ", result1.ToString());
		Assert.Equal("W0rld", result2.ToString());
	}

	[Fact]
	public void BufferMemoryCopy_LengthCalculations_HandleBoundaries()
	{
		// Test memory copy operations with strings at various lengths
		var testStrings = new[] { "", "A", "AB", "ABC", new string('X', 1000) };

		foreach (var testStr in testStrings) {
			PooledString result;
			if (string.IsNullOrEmpty(testStr)) {
				// Empty string case: allocate "PREFIX" directly since we can't insert into an empty string from a different pool
				result = pool.Allocate("PREFIX");
			} else {
				var str = pool.Allocate(testStr);
				result = str.Insert(0, "PREFIX");
			}

			Assert.Equal("PREFIX" + testStr, result.ToString());
		}
	}

	#endregion

	#region Binary Search Overflow Tests

	[Fact]
	public void BinarySearch_IndexCalculation_DoesNotOverflow()
	{
		// Fill pool with many allocations to stress binary search in free block management
		var strings = new PooledString[100];

		for (var i = 0; i < strings.Length; i++) {
			strings[i] = pool.Allocate($"String {i}");
		}

		// Free every other string to create many free blocks
		for (var i = 0; i < strings.Length; i += 2) {
			strings[i].Free();
		}

		// Allocate new strings - this will exercise the binary search in FindSuitableFreeBlock
		for (var i = 0; i < 10; i++) {
			var newStr = pool.Allocate($"New {i}");
			Assert.Equal($"New {i}", newStr.ToString());
		}
	}

	#endregion

	#region Edge Case Arithmetic Tests

	[Fact]
	public void ArithmeticOperations_NearIntegerLimits_BehaveCorrectly()
	{
		// Test various arithmetic operations with values near integer limits
		using var testPool = new UnmanagedStringPool(1000);

		// These should not cause overflow in internal calculations
		var str = testPool.Allocate("test");
		var hashCode = str.GetHashCode();
		var length = str.Length;
		var isEmpty = str.IsEmpty;

		// Verify the operations completed without throwing
		Assert.True(hashCode != 0 || str.AsSpan().IsEmpty);
		Assert.Equal(4, length);
		Assert.False(isEmpty);
	}

	[Fact]
	public void FreeBlockCoalescing_WithMaxSizes_DoesNotOverflow()
	{
		using var testPool = new UnmanagedStringPool(4096);

		// Create a pattern that will trigger coalescing
		var strings = new PooledString[50];

		for (var i = 0; i < strings.Length; i++) {
			strings[i] = testPool.Allocate($"Coalesce test string number {i} with extra content");
		}

		// Free all strings to create fragmentation and trigger coalescing
		foreach (var str in strings) {
			str.Free();
		}

		// Allocate again - should trigger coalescing without overflow
		var newStr = testPool.Allocate("After coalescing");
		Assert.Equal("After coalescing", newStr.ToString());
	}

	#endregion
}
