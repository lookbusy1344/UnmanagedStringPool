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
		var ok = segment.TryAllocate(byteCount: 128, out var ptr);
		Assert.True(ok);
		Assert.Equal(segment.Buffer, ptr);
		Assert.Equal(128, segment.BumpOffset);
	}

	[Fact]
	public void TryAllocate_BeyondCapacity_ReturnsFalse()
	{
		Assert.False(segment.TryAllocate(byteCount: 8192, out _));
	}

	[Fact]
	public void Free_AlignedSize_ReturnsBlockToBin()
	{
		_ = segment.TryAllocate(256, out var ptr);
		segment.Free(ptr, 256);
		Assert.True(segment.TryAllocate(200, out var reused));
		Assert.Equal(ptr, reused);
	}

	[Fact]
	public void Free_AdjacentBlocks_CoalesceOnFree()
	{
		_ = segment.TryAllocate(256, out var a);
		_ = segment.TryAllocate(256, out var b);
		segment.Free(a, 256);
		segment.Free(b, 256);
		Assert.True(segment.TryAllocate(512, out var merged));
		Assert.Equal(a, merged);
	}

	[Fact]
	public void TryAllocate_SplitsLargeFreeBlock()
	{
		_ = segment.TryAllocate(1024, out var ptr);
		segment.Free(ptr, 1024);
		_ = segment.TryAllocate(256, out var first);
		Assert.Equal(ptr, first);
		_ = segment.TryAllocate(256, out var second);
		Assert.Equal(new IntPtr(ptr.ToInt64() + 256), second);
	}

	[Fact]
	public void Contains_PointerInsideBuffer_ReturnsTrue()
	{
		_ = segment.TryAllocate(128, out var ptr);
		Assert.True(segment.Contains(ptr));
	}
}
