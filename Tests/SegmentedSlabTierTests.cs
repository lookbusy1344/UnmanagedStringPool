namespace LookBusy.Test;

using System;
using LookBusy;
using Xunit;

public sealed class SegmentedSlabTierTests : IDisposable
{
	private readonly SegmentedSlabTier tier = new(cellsPerSlab: 4);

	public void Dispose()
	{
		tier.Dispose();
		GC.SuppressFinalize(this);
	}

	[Theory]
	[InlineData(1, 0)]
	[InlineData(8, 0)]
	[InlineData(9, 1)]
	[InlineData(16, 1)]
	[InlineData(17, 2)]
	[InlineData(32, 2)]
	[InlineData(64, 3)]
	[InlineData(65, 4)]
	[InlineData(128, 4)]
	public void ChooseSizeClass_ReturnsSmallestClassFitting(int charCount, int expectedClass) =>
		Assert.Equal(expectedClass, SegmentedSlabTier.ChooseSizeClass(charCount));

	[Fact]
	public void ChooseSizeClass_AboveThreshold_ReturnsSentinel()
	{
		Assert.Equal(-1, SegmentedSlabTier.ChooseSizeClass(129));
		Assert.Equal(-1, SegmentedSlabTier.ChooseSizeClass(1000));
	}

	[Fact]
	public void Allocate_SmallString_ReturnsPointerIntoAnySlab()
	{
		var ptr = tier.Allocate(charCount: 5, out var slabRef);
		Assert.NotEqual(IntPtr.Zero, ptr);
		Assert.NotNull(slabRef);
	}

	[Fact]
	public void Allocate_BeyondSlabCapacity_AllocatesSecondSlab()
	{
		for (var i = 0; i < 4; i++) {
			_ = tier.Allocate(5, out _);
		}
		_ = tier.Allocate(5, out _);
		Assert.True(tier.SlabCount >= 2);
	}

	[Fact]
	public void Free_ReturnsCellToSlab()
	{
		var ptr = tier.Allocate(5, out var slab);
		SegmentedSlabTier.Free(ptr, slab);
		Assert.Equal(4, slab.FreeCells);
	}
}
