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
		var ptr = tier.Allocate(byteCount: 512, out var segment);
		Assert.NotEqual(IntPtr.Zero, ptr);
		Assert.NotNull(segment);
		Assert.Equal(1, tier.SegmentCount);
	}

	[Fact]
	public void Allocate_BeyondSegmentCapacity_AddsNewSegment()
	{
		for (var i = 0; i < 10; ++i) {
			_ = tier.Allocate(1024, out _);
		}
		Assert.True(tier.SegmentCount >= 2);
	}

	[Fact]
	public void Allocate_LargerThanSegmentSize_CreatesOversizeSegment()
	{
		_ = tier.Allocate(8192, out var seg);
		Assert.NotNull(seg);
		Assert.True(seg.Capacity >= 8192);
	}

	[Fact]
	public void Free_ReturnsBlockToOwningSegment()
	{
		var ptr = tier.Allocate(1024, out var seg);
		SegmentedArenaTier.Free(ptr, 1024, seg);
		_ = tier.Allocate(1024, out _);
		Assert.True(seg.BumpOffset <= 2048);
	}

	[Fact]
	public void LocateSegmentByPointer_FindsOwner()
	{
		var ptr1 = tier.Allocate(1024, out var s1);
		for (var i = 0; i < 10; ++i) { _ = tier.Allocate(1024, out _); }
		var found = tier.LocateSegmentByPointer(ptr1);
		Assert.Same(s1, found);
	}
}
