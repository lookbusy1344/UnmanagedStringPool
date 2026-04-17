namespace LookBusy.Test;

using LookBusy;
using Xunit;

public sealed class SegmentedSlotEntryEncodingTests
{
	[Fact]
	public void IsFree_HighBitSet_ReturnsTrue()
	{
		var gen = SegmentedConstants.HighBit | 5u;
		Assert.True(SegmentedSlotEntry.IsFree(gen));
	}

	[Fact]
	public void IsFree_HighBitClear_ReturnsFalse()
	{
		Assert.False(SegmentedSlotEntry.IsFree(5u));
		Assert.False(SegmentedSlotEntry.IsFree(0u));
	}

	[Fact]
	public void GenerationValue_MasksOffHighBit()
	{
		var gen = SegmentedConstants.HighBit | 42u;
		Assert.Equal(42u, SegmentedSlotEntry.GenerationValue(gen));
	}

	[Fact]
	public void MarkFreeAndBumpGen_SetsHighBitAndIncrements()
	{
		var gen = SegmentedSlotEntry.MarkFreeAndBumpGen(5u);
		Assert.True(SegmentedSlotEntry.IsFree(gen));
		Assert.Equal(6u, SegmentedSlotEntry.GenerationValue(gen));
	}

	[Fact]
	public void ClearFreeAndBumpGen_ClearsHighBitAndIncrements()
	{
		var gen = SegmentedSlotEntry.ClearFreeAndBumpGen(SegmentedConstants.HighBit | 5u);
		Assert.False(SegmentedSlotEntry.IsFree(gen));
		Assert.Equal(6u, SegmentedSlotEntry.GenerationValue(gen));
	}
}
