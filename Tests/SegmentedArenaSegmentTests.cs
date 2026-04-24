namespace LookBusy.Test;

using System;
using LookBusy;
using Xunit;

public sealed class SegmentedArenaSegmentTests : IDisposable
{
	private readonly SegmentedArenaSegment segment = new(capacity: 4096);

	public void Dispose()
	{
		segment.Dispose();
		GC.SuppressFinalize(this);
	}

	[Fact]
	public void NewSegment_BumpOffsetZero()
	{
		Assert.Equal(0, segment.BumpOffset);
		Assert.Equal(4096, segment.Capacity);
	}

	[Fact]
	public void TryAllocate_FromEmpty_UsesBump()
	{
		var ok = segment.TryAllocate(byteCount: 128, out var ptr, out var actualBytes);
		Assert.True(ok);
		Assert.Equal(segment.Buffer, ptr);
		Assert.Equal(128, segment.BumpOffset);
		Assert.Equal(128, actualBytes);
	}

	[Fact]
	public void TryAllocate_BeyondCapacity_ReturnsFalse()
	{
		Assert.False(segment.TryAllocate(byteCount: 8192, out _, out _));
	}

	[Fact]
	public void Free_AlignedSize_ReturnsBlockToBin()
	{
		_ = segment.TryAllocate(256, out var ptr, out _);
		segment.Free(ptr, 256);
		Assert.True(segment.TryAllocate(200, out var reused, out _));
		Assert.Equal(ptr, reused);
	}

	[Fact]
	public void Free_AdjacentBlocks_CoalesceOnFree()
	{
		_ = segment.TryAllocate(256, out var a, out _);
		_ = segment.TryAllocate(256, out var b, out _);
		segment.Free(a, 256);
		segment.Free(b, 256);
		Assert.True(segment.TryAllocate(512, out var merged, out _));
		Assert.Equal(a, merged);
	}

	[Fact]
	public void TryAllocate_SplitsLargeFreeBlock()
	{
		_ = segment.TryAllocate(1024, out var ptr, out _);
		segment.Free(ptr, 1024);
		_ = segment.TryAllocate(256, out var first, out var firstActual);
		Assert.Equal(ptr, first);
		Assert.Equal(256, firstActual); // split path: actualBytes == requested aligned size
		_ = segment.TryAllocate(256, out var second, out _);
		Assert.Equal(new IntPtr(ptr.ToInt64() + 256), second);
	}

	[Fact]
	public void TryAllocate_NoSplit_ActualBytesIsFullBlockSize()
	{
		// Create a 24-byte free block, then request 16 bytes.
		// Remainder = 8 < MinArenaBlockBytes (16) → no split; full 24 bytes handed out.
		_ = segment.TryAllocate(24, out var ptr, out _);
		segment.Free(ptr, 24);
		_ = segment.TryAllocate(16, out _, out var actual);
		Assert.Equal(24, actual); // must report the full block, not just the aligned request
	}

	[Fact]
	public void Contains_PointerInsideBuffer_ReturnsTrue()
	{
		_ = segment.TryAllocate(128, out var ptr, out _);
		Assert.True(segment.Contains(ptr));
	}

	// P1-2: boundary-tag coalescing — these tests verify O(1) coalescing via footer reads.

	[Fact]
	public void Free_ForwardCoalesce_MergesWithImmediateSuccessor()
	{
		// Allocate two adjacent blocks, free the second first (puts it in the bin),
		// then free the first — should coalesce forward into a single 512-byte block.
		_ = segment.TryAllocate(256, out var a, out _);
		_ = segment.TryAllocate(256, out var b, out _);
		segment.Free(b, 256);
		segment.Free(a, 256);
		// The merged block must satisfy a 512-byte allocation starting at 'a'.
		Assert.True(segment.TryAllocate(512, out var merged, out _));
		Assert.Equal(a, merged);
	}

	[Fact]
	public void Free_BackwardCoalesce_MergesWithImmediatePredecessor()
	{
		// Allocate two adjacent blocks, free the first (puts it in the bin),
		// then free the second — should coalesce backward into a single 512-byte block.
		_ = segment.TryAllocate(256, out var a, out _);
		_ = segment.TryAllocate(256, out var b, out _);
		segment.Free(a, 256);
		segment.Free(b, 256);
		Assert.True(segment.TryAllocate(512, out var merged, out _));
		Assert.Equal(a, merged);
	}

	[Fact]
	public void Free_ThreeAdjacent_CoalescesBothDirections()
	{
		_ = segment.TryAllocate(256, out var a, out _);
		_ = segment.TryAllocate(256, out var b, out _);
		_ = segment.TryAllocate(256, out var c, out _);
		segment.Free(a, 256);
		segment.Free(c, 256);
		// Free the middle block — should coalesce with both neighbours.
		segment.Free(b, 256);
		Assert.True(segment.TryAllocate(768, out var merged, out _));
		Assert.Equal(a, merged);
	}

	[Fact]
	public void Free_NonAdjacentBlocks_DoNotCoalesce()
	{
		// Use a segment sized to exactly hold 3×256-byte blocks so the bump allocator
		// is exhausted after the initial allocations and only the free list is available.
		using var tight = new SegmentedArenaSegment(capacity: 768);
		_ = tight.TryAllocate(256, out var a, out _);
		_ = tight.TryAllocate(256, out _, out _); // live barrier between a and c
		_ = tight.TryAllocate(256, out var c, out _);
		tight.Free(a, 256);
		tight.Free(c, 256);
		// Bump is exhausted; a and c are not adjacent — 512-byte alloc must fail.
		Assert.False(tight.TryAllocate(512, out _, out _));
	}
}
