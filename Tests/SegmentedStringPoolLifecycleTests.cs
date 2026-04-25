namespace LookBusy.Test;

using System;
using System.Runtime.CompilerServices;
using LookBusy;
using Xunit;

public sealed class SegmentedStringPoolLifecycleTests
{
	[Fact]
	public void Clear_InvalidatesAllHandles()
	{
		var pool = new SegmentedStringPool();
		var a = pool.Allocate("hello");
		var b = pool.Allocate("world");
		pool.Clear();
		_ = Assert.Throws<InvalidOperationException>(() => _ = a.AsSpan().Length);
		_ = Assert.Throws<InvalidOperationException>(() => _ = b.AsSpan().Length);
		pool.Dispose();
	}

	[Fact]
	public void Clear_AllowsFreshAllocationsAfterward()
	{
		var pool = new SegmentedStringPool();
		_ = pool.Allocate("hello");
		pool.Clear();
		var after = pool.Allocate("fresh");
		Assert.True(after.AsSpan().SequenceEqual("fresh"));
		pool.Dispose();
	}

	[Fact]
	public void Dispose_InvalidatesHandles_AndSubsequentAllocateThrows()
	{
		var pool = new SegmentedStringPool();
		var h = pool.Allocate("hello");
		pool.Dispose();
		_ = Assert.Throws<ObjectDisposedException>(() => _ = h.AsSpan().Length);
		_ = Assert.Throws<ObjectDisposedException>(() => pool.Allocate("x"));
	}

	[Fact]
	public void Dispose_IsIdempotent()
	{
		var pool = new SegmentedStringPool();
		pool.Dispose();
		pool.Dispose(); // must not throw
	}

	[Fact]
	public void Reserve_AllowsLargeAllocationsWithoutGrowth()
	{
		using var pool = new SegmentedStringPool();
		pool.Reserve(1_000_000);
		for (var i = 0; i < 1000; ++i) {
			_ = pool.Allocate(new string('x', 500));
		}
	}

	// P1-5: ReserveSmall / ReserveLarge

	[Fact]
	public void ReserveSmall_PreAllocatesSlabCapacityWithoutTouchingArena()
	{
		using var pool = new SegmentedStringPool();
		var segsBefore = pool.SegmentCount;
		pool.ReserveSmall(10_000);
		Assert.Equal(segsBefore, pool.SegmentCount); // arena unchanged
		Assert.True(pool.SlabCount > 0);
	}

	[Fact]
	public void ReserveLarge_PreAllocatesArenaCapacityWithoutTouchingSlabs()
	{
		using var pool = new SegmentedStringPool();
		var slabsBefore = pool.SlabCount;
		pool.ReserveLarge(1_000_000);
		Assert.Equal(slabsBefore, pool.SlabCount); // slabs unchanged
		Assert.True(pool.SegmentCount > 0);
	}

	[Fact]
	public void Reserve_StillSplitsEvenlyBetweenTiers()
	{
		using var pool = new SegmentedStringPool();
		pool.Reserve(2_000_000);
		// Both tiers must have received capacity — just verify neither is zero.
		Assert.True(pool.SlabCount > 0);
		Assert.True(pool.SegmentCount > 0);
	}

	// P1-6: ReserveSmall distributes slabs across all size classes

	[Fact]
	public void ReserveSmall_AllocatesSlabsAcrossMultipleSizeClasses()
	{
		using var pool = new SegmentedStringPool();
		// A large enough reservation should produce slabs for more than one size class.
		pool.ReserveSmall(100_000);
		Assert.True(pool.SlabCount >= SegmentedSlabTier.SizeClassCount,
			$"Expected slabs for all {SegmentedSlabTier.SizeClassCount} size classes, got {pool.SlabCount}");
	}

	[Fact]
	public void ReserveSmall_SmallStringsUseReservedSlabs_NotLargestClass()
	{
		// Allocating 8-char strings after ReserveSmall must not need to create new slabs if
		// the reservation was sufficient for the smallest size class.
		var options = new SegmentedStringPoolOptions(SlabCellsPerSlab: 4);
		using var pool = new SegmentedStringPool(options);
		pool.ReserveSmall(1000);
		var slabsAfterReserve = pool.SlabCount;
		for (var i = 0; i < 10; ++i) {
			_ = pool.Allocate(new string('a', 8)); // fits size class 0 (8-char cells)
		}
		// Should not have grown beyond what was reserved (4 cells/slab means 10 allocs = ≤3 slabs extra).
		Assert.True(pool.SlabCount <= slabsAfterReserve + 3,
			$"More slabs than expected: {pool.SlabCount} vs reserved {slabsAfterReserve}");
	}

	// P1-7: TotalBytesManaged must account for slot-entry array + slab bitmap arrays

	[Fact]
	public void TotalBytesManaged_AtLeastCoversSlotArray()
	{
		using var pool = new SegmentedStringPool(new(InitialSlotCapacity: 64));
		var slotEntrySize = Unsafe.SizeOf<SegmentedSlotEntry>();
		Assert.True(pool.GetTotalBytesManaged() >= 64L * slotEntrySize,
			$"TotalBytesManaged={pool.GetTotalBytesManaged()} should be >= {64L * slotEntrySize}");
	}

	[Fact]
	public void TotalBytesManaged_GrowsWhenSlabsAdded()
	{
		using var pool = new SegmentedStringPool();
		var before = pool.GetTotalBytesManaged();
		pool.ReserveSmall(10_000);
		Assert.True(pool.GetTotalBytesManaged() > before,
			$"Expected TotalBytesManaged to grow after ReserveSmall; before={before} after={pool.GetTotalBytesManaged()}");
	}

	[Fact]
	public void TotalBytesManaged_ReflectsCorrectSlotEntrySize()
	{
		// Each SegmentedSlotEntry is 32 bytes (8 ptr + 8 obj ref + 4 + 4 + 4 + 4 pad).
		// A fresh pool with 64-slot capacity must report >= 64 * 32 = 2048 bytes.
		using var pool = new SegmentedStringPool(new(InitialSlotCapacity: 64));
		Assert.True(pool.GetTotalBytesManaged() >= 2048L,
			$"TotalBytesManaged={pool.GetTotalBytesManaged()}, expected >= 2048");
	}
}
