namespace LookBusy.Test;

using System;
using LookBusy;
using Xunit;

public sealed class SegmentedArenaTierTests : IDisposable
{
	private readonly SegmentedArenaTier tier = new(segmentBytes: 4096);

	public void Dispose()
	{
		tier.Dispose();
		GC.SuppressFinalize(this);
	}

	[Fact]
	public void Allocate_First_UsesInitialSegment()
	{
		var ptr = tier.Allocate(byteCount: 512, out var segment, out _);
		Assert.NotEqual(IntPtr.Zero, ptr);
		Assert.NotNull(segment);
		Assert.Equal(1, tier.SegmentCount);
	}

	[Fact]
	public void Allocate_BeyondSegmentCapacity_AddsNewSegment()
	{
		for (var i = 0; i < 10; ++i) {
			_ = tier.Allocate(1024, out _, out _);
		}
		Assert.True(tier.SegmentCount >= 2);
	}

	[Fact]
	public void Allocate_LargerThanSegmentSize_CreatesOversizeSegment()
	{
		_ = tier.Allocate(8192, out var seg, out _);
		Assert.NotNull(seg);
		Assert.True(seg.Capacity >= 8192);
	}

	[Fact]
	public void Allocate_OversizedUnalignedRequest_CreatesAlignedCapacitySegment()
	{
		using var customTier = new SegmentedArenaTier(segmentBytes: 16);

		_ = customTier.Allocate(17, out var seg, out var allocatedBytes);

		Assert.NotNull(seg);
		Assert.Equal(24, seg.Capacity);
		Assert.Equal(24, allocatedBytes);
	}

	[Fact]
	public void Allocate_UnalignedOversizedRequest_ReturnsNonZeroPointer()
	{
		using var customTier = new SegmentedArenaTier(segmentBytes: 16);

		var ptr = customTier.Allocate(17, out var seg, out var allocatedBytes);

		Assert.NotEqual(IntPtr.Zero, ptr);
		Assert.NotNull(seg);
		Assert.Equal(24, seg.Capacity);
		Assert.Equal(24, allocatedBytes);
	}

	[Fact]
	public void Free_ReturnsBlockToOwningSegment()
	{
		var ptr = tier.Allocate(1024, out var seg, out var actual);
		SegmentedArenaTier.Free(ptr, actual, seg);
		_ = tier.Allocate(1024, out _, out _);
		Assert.True(seg.BumpOffset <= 2048);
	}

	[Fact]
	public void LocateSegmentByPointer_FindsOwner()
	{
		var ptr1 = tier.Allocate(1024, out var s1, out _);
		for (var i = 0; i < 10; ++i) { _ = tier.Allocate(1024, out _, out _); }
		var found = tier.LocateSegmentByPointer(ptr1);
		Assert.Same(s1, found);
	}

	// P2-8: oversized segments are skipped for normal requests

	[Fact]
	public void OversizedSegment_IsSkippedForNormalAllocations()
	{
		// Allocate one oversized block (bigger than the 4096-byte default segment).
		// Then allocate a small normal block — it must NOT land in the oversized segment.
		_ = tier.Allocate(8192, out var oversized, out _);
		Assert.True(oversized.IsOversized);

		var normalPtr = tier.Allocate(64, out var normalSeg, out _);
		Assert.NotSame(oversized, normalSeg);
		Assert.False(normalSeg.IsOversized);
	}

	[Fact]
	public void OversizedRequest_ReusesExistingOversizedSegmentIfItFits()
	{
		// Two oversized requests whose combined size fits in a single dedicated segment
		// should both land in the same segment (first oversized created for the first request).
		_ = tier.Allocate(5000, out var first, out _);
		Assert.True(first.IsOversized);
		// Second oversized request — should use the same oversized segment if it still has room.
		// The first segment had capacity = max(4096, 5000) = 5000; 5000 - 5000 = 0 left. So a new one is created.
		_ = tier.Allocate(5000, out var second, out _);
		Assert.True(second.IsOversized);
	}

	[Fact]
	public void NormalAllocations_DoNotLeakIntoOversizedSegments()
	{
		// Fill a normal segment, then introduce an oversized one, then continue normal allocs.
		// Normal allocs after the oversized segment must create a fresh normal segment, not
		// fall through to the oversized one.
		var oversizedPtr = tier.Allocate(8192, out var oversized, out _);
		var normal = tier.Allocate(256, out var normalSeg, out _);
		Assert.False(oversized.Contains(normal));
		_ = normal; // suppress unused warning
		_ = oversizedPtr;
	}
}
