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

	public SegmentedSlotTable(int initialCapacity)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(initialCapacity, 1);
		slots = new SegmentedSlotEntry[initialCapacity];
		freeHead = SegmentedConstants.NoFreeSlot;
	}

	public int ActiveCount { get; private set; }

	public int Capacity => slots.Length;

	/// <summary>
	/// Pops a slot from the free chain if one is available, otherwise advances <see cref="highWater"/>,
	/// growing the backing array by doubling if needed. Returns the index and the new generation.
	/// </summary>
	public (uint SlotIndex, uint Generation) Allocate(IntPtr ptr, int lengthChars)
	{
		uint slotIndex;
		if (freeHead != SegmentedConstants.NoFreeSlot) {
			// Reuse a previously-freed slot: pop the head of the intrusive free chain.
			// The slot's Ptr field currently stores the next-free-slot index.
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
		++ActiveCount;
		return (slotIndex, newGen);
	}

	/// <summary>
	/// Marks a slot freed and pushes it onto the head of the intrusive free chain.
	/// The generation is bumped so any outstanding <see cref="PooledStringRef"/> with the old generation is immediately stale.
	/// Returns false if the slot is already free or the generation does not match.
	/// </summary>
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
		// Repurpose Ptr to store the next-free-slot index; the real pointer is no longer needed.
		slot.Ptr = new(freeHead);
		slot.LengthChars = 0;
		slot.Generation = bumped;
		freeHead = slotIndex;
		--ActiveCount;
		return true;
	}

	/// <summary>
	/// Returns the slot entry only if it is live and its generation matches.
	/// A mismatch means the ref is stale (the string was freed or the slot reused).
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool TryReadSlot(uint slotIndex, uint generation, out SegmentedSlotEntry entry)
	{
		if (slotIndex >= (uint)highWater) {
			entry = default;
			return false;
		}

		entry = slots[slotIndex];
		if (entry.Generation == generation) {
			return true;
		}

		entry = default;
		return false;
	}

	public ref SegmentedSlotEntry SlotRef(uint slotIndex) => ref slots[slotIndex];

	/// <summary>
	/// Marks every live slot as freed and rebuilds the free chain in index order (0 → 1 → … → highWater−1).
	/// All outstanding <see cref="PooledStringRef"/> handles become stale. Does not shrink the backing array.
	/// </summary>
	public void ClearAllSlots()
	{
		for (var i = 0; i < highWater; ++i) {
			ref var slot = ref slots[i];
			if (!SegmentedSlotEntry.IsFree(slot.Generation)) {
				slot.Generation = SegmentedSlotEntry.MarkFreeAndBumpGen(slot.Generation);
			}

			// Thread the free chain through Ptr in sequential index order.
			var nextIndex = i + 1 < highWater ? i + 1 : (long)SegmentedConstants.NoFreeSlot;
			slot.Ptr = new(nextIndex);
			slot.LengthChars = 0;
		}

		freeHead = highWater == 0 ? SegmentedConstants.NoFreeSlot : 0u;
		ActiveCount = 0;
	}

	/// <summary>
	/// Doubles the slot array. This is the only managed heap allocation that occurs during steady-state pool usage;
	/// it is amortised O(1) per allocation because it happens at powers-of-two boundaries.
	/// </summary>
	private void Grow()
	{
		var newCapacity = slots.Length * 2;
		if ((uint)newCapacity > int.MaxValue) {
			throw new OutOfMemoryException("Slot table capacity exceeded");
		}

		Array.Resize(ref slots, newCapacity);
	}
}
