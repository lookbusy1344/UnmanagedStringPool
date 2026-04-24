namespace LookBusy.Test;

using System;
using LookBusy;
using Xunit;

public sealed class SegmentedSlabTests : IDisposable
{
	private readonly SegmentedSlab slab = new(sizeClass: 0, cellBytes: 16, cellCount: 8);

	public void Dispose()
	{
		slab.Dispose();
		GC.SuppressFinalize(this);
	}

	[Fact]
	public void NewSlab_AllCellsFree()
	{
		Assert.Equal(8, slab.FreeCells);
		Assert.False(slab.IsFull);
	}

	[Fact]
	public void TryAllocateCell_Empty_ReturnsZeroOffset()
	{
		var ok = slab.TryAllocateCell(out var cellIndex);
		Assert.True(ok);
		Assert.Equal(0, cellIndex);
		Assert.Equal(7, slab.FreeCells);
	}

	[Fact]
	public void TryAllocateCell_Sequential_ReturnsIncreasingIndices()
	{
		_ = slab.TryAllocateCell(out var a);
		_ = slab.TryAllocateCell(out var b);
		_ = slab.TryAllocateCell(out var c);
		Assert.Equal(0, a);
		Assert.Equal(1, b);
		Assert.Equal(2, c);
	}

	[Fact]
	public void TryAllocateCell_Full_ReturnsFalse()
	{
		for (var i = 0; i < 8; ++i) {
			Assert.True(slab.TryAllocateCell(out _));
		}
		Assert.True(slab.IsFull);
		Assert.False(slab.TryAllocateCell(out _));
	}

	[Fact]
	public void FreeCell_AllowsReuse()
	{
		_ = slab.TryAllocateCell(out _);
		_ = slab.TryAllocateCell(out var second);
		slab.FreeCell(second);
		Assert.Equal(7, slab.FreeCells);
		_ = slab.TryAllocateCell(out var reused);
		Assert.Equal(second, reused);
	}

	[Fact]
	public void OffsetOfCell_ReturnsCellIndex_TimesCellBytes()
	{
		Assert.Equal(0, slab.OffsetOfCell(0));
		Assert.Equal(16, slab.OffsetOfCell(1));
		Assert.Equal(112, slab.OffsetOfCell(7));
	}

	[Fact]
	public void CellIndexFromOffset_Roundtrips()
	{
		for (var i = 0; i < 8; ++i) {
			Assert.Equal(i, slab.CellIndexFromOffset(slab.OffsetOfCell(i)));
		}
	}
}
