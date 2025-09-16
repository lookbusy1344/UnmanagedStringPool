namespace LookBusy.Test;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

public sealed class ClearMethodTests : IDisposable
{
	private readonly UnmanagedStringPool pool;

	public ClearMethodTests() => pool = new(1024);

	public void Dispose()
	{
		pool?.Dispose();
		GC.SuppressFinalize(this);
	}

	#region Basic Clear Functionality

	[Fact]
	public void Clear_EmptyPool_DoesNotThrow()
	{
		// Act
		pool.Clear();

		// Assert
		Assert.Equal(0, pool.ActiveAllocations);
		Assert.Equal(1024, pool.FreeSpaceChars);
	}

	[Fact]
	public void Clear_WithAllocatedStrings_ResetsPool()
	{
		// Arrange
		var str1 = pool.Allocate("Hello");
		var str2 = pool.Allocate("World");
		var str3 = pool.Allocate("Test");
		Assert.Equal(3, pool.ActiveAllocations);

		// Act
		pool.Clear();

		// Assert
		Assert.Equal(0, pool.ActiveAllocations);
		Assert.Equal(1024, pool.FreeSpaceChars);
		Assert.Equal(1024, pool.EndBlockSizeChars);
	}

	[Fact]
	public void Clear_MultipleTimes_DoesNotThrow()
	{
		// Arrange
		pool.Allocate("Test");

		// Act & Assert - should not throw
		pool.Clear();
		pool.Clear();
		pool.Clear();

		Assert.Equal(0, pool.ActiveAllocations);
	}

	#endregion

	#region Old PooledString Invalidation

	[Fact]
	public void Clear_InvalidatesOldPooledStrings_ThrowsOnAccess()
	{
		// Arrange
		var str = pool.Allocate("Hello World");
		Assert.Equal("Hello World", str.ToString());

		// Act
		pool.Clear();

		// Assert - accessing old string should throw
		Assert.Throws<ArgumentException>(() => str.ToString());
		Assert.Throws<ArgumentException>(() => str.Length);
		Assert.Throws<ArgumentException>(() => str.AsSpan());
	}

	[Fact]
	public void Clear_OldStringComparisons_Throw()
	{
		// Arrange
		var str1 = pool.Allocate("Test");
		var str2 = pool.Allocate("Test");
		Assert.True(str1.Equals(str2)); // Should be equal before clear

		// Act
		pool.Clear();

		// Assert - PooledString comparisons throw when accessing cleared strings
		Assert.Throws<ArgumentException>(() => str1.Equals(str2));

		// Object.Equals doesn't throw, it just returns false
		Assert.False(str1.Equals((object)"Test"));
	}

	[Fact]
	public void Clear_OldStringOperations_Throw()
	{
		// Arrange
		var str = pool.Allocate("Hello World");

		// Act
		pool.Clear();

		// Assert - string operations should throw
		Assert.Throws<ArgumentException>(() => str.IndexOf("o".AsSpan()));
		Assert.Throws<ArgumentException>(() => str.IndexOf("World".AsSpan()));
		Assert.Throws<ArgumentException>(() => str.LastIndexOf("o".AsSpan()));
		Assert.Throws<ArgumentException>(() => str.Contains("Hello"));
		Assert.Throws<ArgumentException>(() => str.StartsWith("Hello"));
		Assert.Throws<ArgumentException>(() => str.EndsWith("World"));
	}

	[Fact]
	public void Clear_OldStringManipulations_Throw()
	{
		// Arrange
		var str = pool.Allocate("Hello World");

		// Act
		pool.Clear();

		// Assert - manipulations should throw
		Assert.Throws<ArgumentException>(() => str.SubstringSpan(0, 5));
		Assert.Throws<ArgumentException>(() => str.Replace("Hello", "Hi"));
		Assert.Throws<ArgumentException>(() => str.Insert(5, " Beautiful"));
	}

	[Fact]
	public void Clear_DisposingOldString_DoesNotThrow()
	{
		// Arrange
		var str = pool.Allocate("Test");

		// Act
		pool.Clear();

		// Assert - disposing should be safe even after clear
		str.Dispose(); // Should not throw
	}

	#endregion

	#region ID Counter Preservation

	[Fact]
	public void Clear_PreservesIdCounter_NewAllocationsGetHigherIds()
	{
		// Arrange
		var str1 = pool.Allocate("First");
		var id1 = str1.AllocationId;

		// Act
		pool.Clear();
		var str2 = pool.Allocate("Second");
		var id2 = str2.AllocationId;

		// Assert - new ID should be higher than old ID
		Assert.True(id2 > id1, $"New ID {id2} should be greater than old ID {id1}");
	}

	[Fact]
	public void Clear_OldIdsNeverReused()
	{
		// Arrange
		var ids = new uint[10];
		for (var i = 0; i < 5; i++) {
			ids[i] = pool.Allocate($"String{i}").AllocationId;
		}

		// Act
		pool.Clear();

		// Allocate more strings
		for (var i = 5; i < 10; i++) {
			ids[i] = pool.Allocate($"String{i}").AllocationId;
		}

		// Assert - all IDs should be unique
		Assert.Equal(ids.Length, ids.Distinct().Count());

		// New IDs should be higher than old ones
		for (var i = 0; i < 5; i++) {
			for (var j = 5; j < 10; j++) {
				Assert.True(ids[j] > ids[i],
					$"New ID {ids[j]} at index {j} should be greater than old ID {ids[i]} at index {i}");
			}
		}
	}

	#endregion

	#region Memory and Fragmentation

	[Fact]
	public void Clear_ResetsFragmentation()
	{
		// Arrange - create fragmentation
		var strings = new PooledString[10];
		for (var i = 0; i < 10; i++) {
			strings[i] = pool.Allocate($"String number {i}");
		}

		// Free every other string to create fragmentation
		for (var i = 0; i < 10; i += 2) {
			strings[i].Dispose();
		}

		Assert.True(pool.FragmentationPercentage > 0);

		// Act
		pool.Clear();

		// Assert
		Assert.Equal(0, pool.FragmentationPercentage);
	}

	[Fact]
	public void Clear_AllowsReuseOfMemory()
	{
		// Arrange
		var initialFreeSpace = pool.FreeSpaceChars;
		pool.Allocate("This takes up some space");
		var afterAllocation = pool.FreeSpaceChars;
		Assert.True(afterAllocation < initialFreeSpace);

		// Act
		pool.Clear();

		// Assert - free space should be restored
		Assert.Equal(initialFreeSpace, pool.FreeSpaceChars);

		// Should be able to allocate again
		var newStr = pool.Allocate("New allocation after clear");
		Assert.Equal("New allocation after clear", newStr.ToString());
	}

	[Fact]
	public void Clear_BufferDumpShowsEmpty()
	{
		// Arrange
		pool.Allocate("Some content");
		Assert.NotEqual(string.Empty, pool.DumpBufferAsString());

		// Act
		pool.Clear();

		// Assert
		Assert.Equal(string.Empty, pool.DumpBufferAsString());
	}

	#endregion

	#region Edge Cases

	[Fact]
	public void Clear_AfterDefragmentation_WorksCorrectly()
	{
		// Arrange - force defragmentation
		var strings = new PooledString[50];
		for (var i = 0; i < 50; i++) {
			strings[i] = pool.Allocate($"String {i}");
		}

		// Free some to create fragmentation
		for (var i = 0; i < 50; i += 3) {
			strings[i].Dispose();
		}

		// This might trigger defragmentation
		pool.DefragmentAndGrowPool(0);

		// Act
		pool.Clear();

		// Assert
		Assert.Equal(0, pool.ActiveAllocations);
		Assert.Equal(1024, pool.FreeSpaceChars);
	}

	[Fact]
	public void Clear_WithMixedSizeAllocations_ResetsCorrectly()
	{
		// Arrange - allocate strings of various sizes
		pool.Allocate("A");
		pool.Allocate("Medium string");
		pool.Allocate("A much longer string that takes up more space in the pool");
		pool.Allocate("");
		pool.Allocate("Another medium one");

		// Act
		pool.Clear();

		// Assert
		Assert.Equal(0, pool.ActiveAllocations);
		Assert.Equal(1024, pool.FreeSpaceChars);
	}

	[Fact]
	public void Clear_OnDisposedPool_ThrowsObjectDisposedException()
	{
		// Arrange
		var tempPool = new UnmanagedStringPool(100);
		tempPool.Dispose();

		// Act & Assert
		Assert.Throws<ObjectDisposedException>(() => tempPool.Clear());
	}

	#endregion

	#region Concurrent Access After Clear

	[Fact]
	public async Task Clear_InvalidatesStringsAcrossThreadsAsync()
	{
		// Arrange
		var str = pool.Allocate("Shared string");
		var clearExecuted = false;
		var exceptionThrown = false;

		// Act - access string from another thread after clear
		var task = Task.Run(() => {
			// Wait for clear to be executed
			while (!clearExecuted) {
				Thread.Yield();
			}

			try {
				_ = str.ToString(); // Should throw
			}
			catch (ArgumentException) {
				exceptionThrown = true;
			}
		});

		pool.Clear();
		clearExecuted = true;

		await task;

		// Assert
		Assert.True(exceptionThrown, "Expected ArgumentException when accessing cleared string from another thread");
	}

	#endregion
}
