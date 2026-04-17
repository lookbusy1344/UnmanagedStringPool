namespace LookBusy;

using System;
using System.Runtime.CompilerServices;

/// <summary>
/// Dynamically-growing array of <see cref="SegmentedSlotEntry"/>. Slots form an intrusive
/// free-list via the generation high bit + Ptr field when freed. Grows by doubling.
/// </summary>
internal sealed class SegmentedSlotTable
{
	private SegmentedSlotEntry[] slots;
	private int highWater;
	private uint freeHead;
	private int activeCount;

	public SegmentedSlotTable(int initialCapacity)
	{
		if (initialCapacity < 1) {
			throw new ArgumentOutOfRangeException(nameof(initialCapacity));
		}
		slots = new SegmentedSlotEntry[initialCapacity];
		freeHead = SegmentedConstants.NoFreeSlot;
	}

	public int ActiveCount => activeCount;

	public int Capacity => slots.Length;

	public (uint SlotIndex, uint Generation) Allocate(IntPtr ptr, int lengthChars)
	{
		uint slotIndex;
		if (freeHead != SegmentedConstants.NoFreeSlot) {
			slotIndex = freeHead;
			freeHead = (uint)slots[slotIndex].Ptr.ToInt64();
		} else {
			if (highWater == slots.Length) {
				Grow();
			}
			slotIndex = (uint)highWater;
			++highWater;
		}

		ref var slot = ref slots[slotIndex];
		var newGen = SegmentedSlotEntry.ClearFreeAndBumpGen(slot.Generation);
		slot.Ptr = ptr;
		slot.LengthChars = lengthChars;
		slot.Generation = newGen;
		++activeCount;
		return (slotIndex, newGen);
	}

	public bool Free(uint slotIndex, uint generation)
	{
		if (slotIndex >= (uint)highWater) {
			return false;
		}
		ref var slot = ref slots[slotIndex];
		if (SegmentedSlotEntry.IsFree(slot.Generation)) {
			return false;
		}
		if (slot.Generation != generation) {
			return false;
		}
		var bumped = SegmentedSlotEntry.MarkFreeAndBumpGen(slot.Generation);
		slot.Ptr = new IntPtr((long)freeHead);
		slot.LengthChars = 0;
		slot.Generation = bumped;
		freeHead = slotIndex;
		--activeCount;
		return true;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool TryReadSlot(uint slotIndex, uint generation, out SegmentedSlotEntry entry)
	{
		if (slotIndex >= (uint)highWater) {
			entry = default;
			return false;
		}
		entry = slots[slotIndex];
		if (entry.Generation != generation) {
			entry = default;
			return false;
		}
		return true;
	}

	public ref SegmentedSlotEntry SlotRef(uint slotIndex) => ref slots[slotIndex];

	public void ClearAllSlots()
	{
		for (var i = 0; i < highWater; i++) {
			ref var slot = ref slots[i];
			if (!SegmentedSlotEntry.IsFree(slot.Generation)) {
				slot.Generation = SegmentedSlotEntry.MarkFreeAndBumpGen(slot.Generation);
			}
			var nextIndex = i + 1 < highWater ? (long)(i + 1) : (long)SegmentedConstants.NoFreeSlot;
			slot.Ptr = new IntPtr(nextIndex);
			slot.LengthChars = 0;
		}
		freeHead = highWater == 0 ? SegmentedConstants.NoFreeSlot : 0u;
		activeCount = 0;
	}

	private void Grow()
	{
		var newCapacity = slots.Length * 2;
		if ((uint)newCapacity > (uint)int.MaxValue) {
			throw new OutOfMemoryException("Slot table capacity exceeded");
		}
		Array.Resize(ref slots, newCapacity);
	}
}
