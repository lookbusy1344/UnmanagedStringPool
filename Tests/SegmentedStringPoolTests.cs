namespace LookBusy.Test;

using System;
using LookBusy;
using Xunit;

public sealed class SegmentedStringPoolTests : IDisposable
{
	private readonly SegmentedStringPool pool = new();

	public void Dispose()
	{
		pool.Dispose();
		GC.SuppressFinalize(this);
	}

	[Fact]
	public void Constructor_Default_ZeroActive()
	{
		Assert.Equal(0, pool.ActiveAllocations);
	}

	[Fact]
	public void Allocate_EmptySpan_ReturnsEmptyRef()
	{
		var r = pool.Allocate(ReadOnlySpan<char>.Empty);
		Assert.True(r.IsEmpty);
		Assert.Equal(0, pool.ActiveAllocations);
	}

	[Fact]
	public void Allocate_ShortString_ReturnsValidRef()
	{
		var r = pool.Allocate("Hello");
		Assert.False(r.IsEmpty);
		Assert.Same(pool, r.Pool);
		Assert.True(r.Generation >= 1u);
		Assert.Equal(1, pool.ActiveAllocations);
	}

	[Fact]
	public void Allocate_AtOrBelowSmallThreshold_UsesSlab()
	{
		var r = pool.Allocate(new string('x', 128));
		Assert.False(r.IsEmpty);
		Assert.True(pool.SlabCount >= 1);
		Assert.Equal(0, pool.SegmentCount);
	}

	// Task 7 tests
	[Fact]
	public void Allocate_AboveSmallThreshold_UsesArena()
	{
		var r = pool.Allocate(new string('y', 256));
		Assert.False(r.IsEmpty);
		Assert.True(pool.SegmentCount >= 1);
	}

	[Fact]
	public void Allocate_VeryLargeString_CreatesOversizeSegment()
	{
		var big = new string('z', 2_000_000);
		var r = pool.Allocate(big);
		Assert.False(r.IsEmpty);
		Assert.True(pool.SegmentCount >= 1);
	}

	[Theory]
	[InlineData(128)]
	[InlineData(129)]
	public void Allocate_BoundaryLengths_RouteToCorrectTier(int length)
	{
		var r = pool.Allocate(new string('x', length));
		Assert.False(r.IsEmpty);
		if (length <= 128) {
			Assert.True(pool.SlabCount >= 1);
		} else {
			Assert.True(pool.SegmentCount >= 1);
		}
	}

	// Task 8 tests
	[Fact]
	public void Free_ValidHandle_ReturnsMemoryToPool()
	{
		var r = pool.Allocate("short");
		var active = pool.ActiveAllocations;
		pool.FreeSlot(r.SlotIndex, r.Generation);
		Assert.Equal(active - 1, pool.ActiveAllocations);
	}

	[Fact]
	public void Free_StaleHandle_IsNoop()
	{
		var r = pool.Allocate("short");
		pool.FreeSlot(r.SlotIndex, r.Generation);
		pool.FreeSlot(r.SlotIndex, r.Generation);
		Assert.Equal(0, pool.ActiveAllocations);
	}

	[Fact]
	public void ReadSlot_ValidHandle_ReturnsContent()
	{
		var r = pool.Allocate("hello");
		var span = pool.ReadSlot(r.SlotIndex, r.Generation);
		Assert.True(span.SequenceEqual("hello"));
	}

	[Fact]
	public void ReadSlot_ArenaAllocation_ReturnsContent()
	{
		var big = new string('q', 200);
		var r = pool.Allocate(big);
		var span = pool.ReadSlot(r.SlotIndex, r.Generation);
		Assert.True(span.SequenceEqual(big));
	}

	[Fact]
	public void ReadSlot_StaleHandle_Throws()
	{
		var r = pool.Allocate("hello");
		pool.FreeSlot(r.SlotIndex, r.Generation);
		_ = Assert.Throws<InvalidOperationException>(() => pool.ReadSlot(r.SlotIndex, r.Generation));
	}

	[Fact]
	public void GetLength_ValidHandle_ReturnsCharCount()
	{
		var r = pool.Allocate("hello");
		Assert.Equal(5, pool.GetLength(r.SlotIndex, r.Generation));
	}

	// P0-1: options constructor must be public
	[Fact]
	public void Constructor_WithOptions_SmallThresholdRoutesToArena()
	{
		var opts = new SegmentedStringPoolOptions(SmallStringThresholdChars: 4);
		using var customPool = new SegmentedStringPool(opts);

		// 5-char string exceeds the threshold of 4 → must go to arena
		_ = customPool.Allocate("hello");

		Assert.Equal(0, customPool.SlabCount);
		Assert.True(customPool.SegmentCount >= 1);
	}

	[Fact]
	public void Constructor_WithOptions_SmallThresholdRoutesToSlab()
	{
		var opts = new SegmentedStringPoolOptions(SmallStringThresholdChars: 4);
		using var customPool = new SegmentedStringPool(opts);

		// 4-char string is at the threshold → must go to slab
		_ = customPool.Allocate("hi!!");

		Assert.True(customPool.SlabCount >= 1);
		Assert.Equal(0, customPool.SegmentCount);
	}
}
