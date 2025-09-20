namespace LookBusy.Tests;

using Xunit;

/// <summary>
/// Tests for PooledString copy behavior and disposal semantics.
/// These tests document and verify that PooledString copies share the same allocation,
/// and disposing any copy invalidates all copies.
/// </summary>
public class CopyBehaviorTests {
	/// <summary>
	/// Verify that copying a PooledString results in both instances sharing the same allocation ID
	/// </summary>
	[Fact]
	public void CopySharing_CopiedPooledStrings_ShareSameAllocationId() {
		using var pool = new UnmanagedStringPool(1024);
		var original = pool.Allocate("Hello World");
		var copy = original;

		// Both should have the same allocation ID
		Assert.Equal(original.AllocationId, copy.AllocationId);
		Assert.Equal(original.Pool, copy.Pool);

		// Both should return the same content
		Assert.Equal(original.ToString(), copy.ToString());
	}

	/// <summary>
	/// Verify that disposing the original PooledString invalidates all copies
	/// </summary>
	[Fact]
	public void DisposalInvalidation_DisposingOriginal_InvalidatesAllCopies() {
		using var pool = new UnmanagedStringPool(1024);
		var original = pool.Allocate("Test String");
		var copy1 = original;
		var copy2 = copy1;

		// All copies should be valid initially
		Assert.Equal("Test String", original.ToString());
		Assert.Equal("Test String", copy1.ToString());
		Assert.Equal("Test String", copy2.ToString());

		// Dispose the original
		original.Dispose();

		// All copies should now be invalid
		Assert.Throws<ArgumentException>(() => original.AsSpan());
		Assert.Throws<ArgumentException>(() => copy1.AsSpan());
		Assert.Throws<ArgumentException>(() => copy2.AsSpan());
	}

	/// <summary>
	/// Verify that disposing a copy invalidates the original and all other copies
	/// </summary>
	[Fact]
	public void DisposalInvalidation_DisposingCopy_InvalidatesOriginalAndAllCopies() {
		using var pool = new UnmanagedStringPool(1024);
		var original = pool.Allocate("Another Test");
		var copy1 = original;
		var copy2 = original;

		// Dispose a copy (not the original)
		copy1.Dispose();

		// All instances should now be invalid
		Assert.Throws<ArgumentException>(() => original.AsSpan());
		Assert.Throws<ArgumentException>(() => copy1.AsSpan());
		Assert.Throws<ArgumentException>(() => copy2.AsSpan());
	}

	/// <summary>
	/// Verify that multiple disposals are safe (idempotent)
	/// </summary>
	[Fact]
	public void MultipleDisposals_DisposingMultipleTimes_IsSafe() {
		using var pool = new UnmanagedStringPool(1024);
		var original = pool.Allocate("Multiple Disposal Test");
		var copy = original;

		// First disposal
		original.Dispose();

		// Additional disposals should not throw
		original.Dispose();  // Dispose original again
		copy.Dispose();      // Dispose the copy
		original.Free();     // Use Free() instead of Dispose()
		copy.Free();         // Free the copy

		// All should still be invalid
		Assert.Throws<ArgumentException>(() => original.AsSpan());
		Assert.Throws<ArgumentException>(() => copy.AsSpan());
	}

	/// <summary>
	/// Verify that PooledString has value semantics but shares allocation
	/// </summary>
	[Fact]
	public void ValueSemantics_StructCopies_MaintainValueSemantics() {
		using var pool = new UnmanagedStringPool(1024);
		var str1 = pool.Allocate("Value Semantics");
		var str2 = str1;

		// They are equal as structs (value equality)
		Assert.Equal(str1, str2);

		// But they share the same underlying allocation
		Assert.Equal(str1.AllocationId, str2.AllocationId);

		// Modifying one via operations creates a new allocation
		var str3 = str1.Insert(0, "Testing ");
		Assert.NotEqual(str1.AllocationId, str3.AllocationId);
		Assert.Equal("Testing Value Semantics", str3.ToString());
		Assert.Equal("Value Semantics", str1.ToString());  // Original unchanged
		Assert.Equal("Value Semantics", str2.ToString());  // Copy unchanged
	}

	/// <summary>
	/// Test copy behavior with empty strings
	/// </summary>
	[Fact]
	public void EmptyStrings_CopyingEmptyString_BehavesCorrectly() {
		using var pool = new UnmanagedStringPool(1024);
		var empty1 = pool.Allocate("");
		var empty2 = empty1;

		// Both should have the empty string allocation ID
		Assert.Equal(UnmanagedStringPool.EmptyStringAllocationId, empty1.AllocationId);
		Assert.Equal(UnmanagedStringPool.EmptyStringAllocationId, empty2.AllocationId);

		// Disposal should be safe
		empty1.Dispose();
		empty2.Dispose();  // Should not throw

		// Empty strings don't have actual allocations to invalidate
		// But after pool disposal, operations requiring the pool will fail
		pool.Dispose();
		Assert.Throws<ObjectDisposedException>(() => empty1.Insert(0, "text"));
		Assert.Throws<ObjectDisposedException>(() => empty2.Insert(0, "text"));
	}

	/// <summary>
	/// Test that operations creating new PooledStrings don't affect copies of the original
	/// </summary>
	[Fact]
	public void Operations_CreatingNewPooledStrings_DontAffectOriginalCopies() {
		using var pool = new UnmanagedStringPool(1024);
		var original = pool.Allocate("Original");
		var copy = original;

		// Operations that create new PooledStrings
		var inserted = original.Insert(8, " Text");
		var replaced = original.Replace("Original", "Modified");

		// Original and copy should still be valid and unchanged
		Assert.Equal("Original", original.ToString());
		Assert.Equal("Original", copy.ToString());

		// New strings should have different allocation IDs
		Assert.NotEqual(original.AllocationId, inserted.AllocationId);
		Assert.NotEqual(original.AllocationId, replaced.AllocationId);

		// Disposing the new strings doesn't affect original/copy
		inserted.Dispose();
		replaced.Dispose();
		Assert.Equal("Original", original.ToString());
		Assert.Equal("Original", copy.ToString());
	}

	/// <summary>
	/// Test copy behavior across using blocks
	/// </summary>
	[Fact]
	public void UsingBlocks_CopyAcrossUsingBlocks_InvalidatesCorrectly() {
		using var pool = new UnmanagedStringPool(1024);
		PooledString copyOutsideUsing;

		using (var original = pool.Allocate("Using Block Test")) {
			copyOutsideUsing = original;
			Assert.Equal("Using Block Test", copyOutsideUsing.ToString());
		}

		// After the using block, original is disposed, so copy is invalid
		Assert.Throws<ArgumentException>(() => copyOutsideUsing.AsSpan());
	}

	/// <summary>
	/// Test that Free() and Dispose() have identical behavior regarding copies
	/// </summary>
	[Fact]
	public void FreeVsDispose_BothMethods_HaveSameCopyInvalidationBehavior() {
		using var pool = new UnmanagedStringPool(2048);

		// Test Free()
		var str1 = pool.Allocate("Test Free");
		var copy1 = str1;
		str1.Free();
		Assert.Throws<ArgumentException>(() => str1.AsSpan());
		Assert.Throws<ArgumentException>(() => copy1.AsSpan());

		// Test Dispose()
		var str2 = pool.Allocate("Test Dispose");
		var copy2 = str2;
		str2.Dispose();
		Assert.Throws<ArgumentException>(() => str2.AsSpan());
		Assert.Throws<ArgumentException>(() => copy2.AsSpan());
	}

	/// <summary>
	/// Verify behavior when copying between different variables and collections
	/// </summary>
	[Fact]
	public void Collections_StoringCopiesInCollections_ShareAllocation() {
		using var pool = new UnmanagedStringPool(1024);
		var original = pool.Allocate("Collection Test");

		// Store in various collections
		var list = new List<PooledString> { original, original };
		var array = new[] { original, original };
		var dict = new Dictionary<int, PooledString> {
			[0] = original,
			[1] = original
		};

		// All should share the same allocation
		Assert.All(list, ps => Assert.Equal(original.AllocationId, ps.AllocationId));
		Assert.All(array, ps => Assert.Equal(original.AllocationId, ps.AllocationId));
		Assert.All(dict.Values, ps => Assert.Equal(original.AllocationId, ps.AllocationId));

		// Disposing original invalidates all
		original.Dispose();

		Assert.All(list, ps => Assert.Throws<ArgumentException>(() => ps.AsSpan()));
		Assert.All(array, ps => Assert.Throws<ArgumentException>(() => ps.AsSpan()));
		Assert.All(dict.Values, ps => Assert.Throws<ArgumentException>(() => ps.AsSpan()));
	}

	/// <summary>
	/// Document that assignment creates a copy that shares allocation
	/// </summary>
	[Fact]
	public void Assignment_SimpleAssignment_CreatesCopyWithSharedAllocation() {
		using var pool = new UnmanagedStringPool(1024);
		var a = pool.Allocate("A");
		var b = pool.Allocate("B");

		// Assignment copies the struct
		var originalAId = a.AllocationId;
		a = b;  // Now a is a copy of b

		// a now shares b's allocation
		Assert.Equal(b.AllocationId, a.AllocationId);
		Assert.Equal("B", a.ToString());

		// The original allocation from "A" is now unreferenced but still allocated
		// (would need explicit disposal or pool cleanup to free it)

		// Disposing either a or b invalidates both
		b.Dispose();
		Assert.Throws<ArgumentException>(() => a.AsSpan());
		Assert.Throws<ArgumentException>(() => b.AsSpan());
	}
}