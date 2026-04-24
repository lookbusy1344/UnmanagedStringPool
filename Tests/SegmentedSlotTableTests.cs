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

public sealed class SegmentedSlotTableTests
{
	[Fact]
	public void NewTable_ActiveCountIsZero()
	{
		var table = new SegmentedSlotTable(16);
		Assert.Equal(0, table.ActiveCount);
	}

	[Fact]
	public void Allocate_FirstSlot_ReturnsSlotZeroAndGenOne()
	{
		var table = new SegmentedSlotTable(16);
		var (slot, gen) = table.Allocate(ptr: (IntPtr)0x1000, lengthChars: 5, owner: null, allocatedBytes: 0);
		Assert.Equal(0u, slot);
		Assert.Equal(1u, gen);
		Assert.Equal(1, table.ActiveCount);
	}

	[Fact]
	public void Allocate_Multiple_AssignsSequentialSlots()
	{
		var table = new SegmentedSlotTable(16);
		var (s0, _) = table.Allocate((IntPtr)0x100, 1, null, 0);
		var (s1, _) = table.Allocate((IntPtr)0x200, 2, null, 0);
		var (s2, _) = table.Allocate((IntPtr)0x300, 3, null, 0);
		Assert.Equal(0u, s0);
		Assert.Equal(1u, s1);
		Assert.Equal(2u, s2);
	}

	[Fact]
	public void TryReadSlot_ValidHandle_ReturnsPtrAndLength()
	{
		var table = new SegmentedSlotTable(16);
		var (slot, gen) = table.Allocate((IntPtr)0xABCD, 7, null, 0);
		var ok = table.TryReadSlot(slot, gen, out var entry);
		Assert.True(ok);
		Assert.Equal((IntPtr)0xABCD, entry.Ptr);
		Assert.Equal(7, entry.LengthChars);
	}

	[Fact]
	public void Free_BumpsGeneration_AndMarksFree()
	{
		var table = new SegmentedSlotTable(16);
		var (slot, gen) = table.Allocate((IntPtr)0x100, 1, null, 0);
		var freed = table.Free(slot, gen);
		Assert.True(freed);
		Assert.Equal(0, table.ActiveCount);
	}

	[Fact]
	public void Free_StaleGeneration_ReturnsFalse_AndDoesNotDoubleFree()
	{
		var table = new SegmentedSlotTable(16);
		var (slot, gen) = table.Allocate((IntPtr)0x100, 1, null, 0);
		Assert.True(table.Free(slot, gen));
		Assert.False(table.Free(slot, gen));     // already freed, handle stale
		Assert.Equal(0, table.ActiveCount);
	}

	[Fact]
	public void TryReadSlot_StaleHandle_ReturnsFalse()
	{
		var table = new SegmentedSlotTable(16);
		var (slot, gen) = table.Allocate((IntPtr)0x100, 1, null, 0);
		_ = table.Free(slot, gen);
		Assert.False(table.TryReadSlot(slot, gen, out _));
	}

	[Fact]
	public void Allocate_AfterFree_ReusesSlotWithNewGeneration()
	{
		var table = new SegmentedSlotTable(16);
		var (s0, g0) = table.Allocate((IntPtr)0x100, 1, null, 0);
		_ = table.Free(s0, g0);
		var (s1, g1) = table.Allocate((IntPtr)0x200, 2, null, 0);
		Assert.Equal(s0, s1);
		Assert.NotEqual(g0, g1);                 // generation incremented twice (free+alloc)
	}

	[Fact]
	public void Allocate_BeyondInitialCapacity_Grows()
	{
		var table = new SegmentedSlotTable(initialCapacity: 4);
		for (var i = 0; i < 10; ++i) {
			_ = table.Allocate((IntPtr)(0x100 + i), 1, null, 0);
		}
		Assert.Equal(10, table.ActiveCount);
	}

	[Fact]
	public void TryReadSlot_OutOfRangeSlotIndex_ReturnsFalse()
	{
		var table = new SegmentedSlotTable(16);
		Assert.False(table.TryReadSlot(99u, 1u, out _));
	}
}
