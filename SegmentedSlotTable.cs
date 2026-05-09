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
	private readonly int initialCapacity;

	public SegmentedSlotTable(int initialCapacity)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(initialCapacity, 1);
		this.initialCapacity = initialCapacity;
		slots = new SegmentedSlotEntry[initialCapacity];
		freeHead = SegmentedConstants.NoFreeSlot;
	}

	public int ActiveCount { get; private set; }

	public int Capacity => slots.Length;

	/// <summary>
	/// Pops a slot from the free chain if one is available, otherwise advances <see cref="highWater"/>,
	/// growing the backing array by doubling if needed. Returns the index and the new generation.
	/// </summary>
	public (uint SlotIndex, uint Generation) Allocate(IntPtr ptr, int lengthChars, object? owner, int allocatedBytes)
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
		slot.Owner = owner;
		slot.AllocatedBytes = allocatedBytes;
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
		slot.Owner = null;       // release the slab/segment reference so it is not rooted here
		slot.AllocatedBytes = 0;
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
		// When already empty skip the scan: every slot is already free, owners are null,
		// and generations already have the high bit set.
		if (ActiveCount > 0) {
			for (var i = 0; i < highWater; ++i) {
				ref var slot = ref slots[i];
				if (!SegmentedSlotEntry.IsFree(slot.Generation)) {
					slot.Generation = SegmentedSlotEntry.MarkFreeAndBumpGen(slot.Generation);
				}

				slot.Ptr = 0;
				slot.LengthChars = 0;
				slot.Owner = null;
				slot.AllocatedBytes = 0;
			}
		}

		// Reset to pristine: highWater=0 means MaybeShrink can always collapse the array,
		// and subsequent Allocate calls bump from 0 just like a freshly-constructed table.
		highWater = 0;
		freeHead = SegmentedConstants.NoFreeSlot;
		ActiveCount = 0;
		MaybeShrink();
	}

	private void Grow()
	{
		var newCapacity = slots.Length * 2;
		if ((uint)newCapacity > int.MaxValue) {
			throw new OutOfMemoryException("Slot table capacity exceeded");
		}

		Array.Resize(ref slots, newCapacity);
	}

	// Halves the backing array when the table is sparse and has grown past its initial size.
	// Conditions: ActiveCount < Capacity/4 (sparse enough) and highWater <= Capacity/2 (no live
	// slot index falls in the upper half, so truncation is safe). Never shrinks below initialCapacity.
	// Halves the backing array repeatedly while the table is sparse and highWater fits in the smaller
	// half. Loops so that a Clear() after a large peak collapses all the way back to initialCapacity
	// in one shot rather than requiring one Free call per halving step.
	private void MaybeShrink()
	{
		while (slots.Length > initialCapacity && ActiveCount < slots.Length / 4) {
			var newCapacity = Math.Max(initialCapacity, slots.Length / 2);
			if (highWater > newCapacity) {
				break; // live slot indices in the upper half — cannot truncate
			}

			// Rebuild the free chain within [0, newCapacity). Chain links written during Free may
			// point into the soon-to-be-removed upper half; a fresh pass avoids out-of-bounds reads.
			freeHead = SegmentedConstants.NoFreeSlot;
			for (var i = highWater - 1; i >= 0; --i) {
				ref var slot = ref slots[i];
				if (SegmentedSlotEntry.IsFree(slot.Generation)) {
					slot.Ptr = new((long)freeHead);
					freeHead = (uint)i;
				}
			}

			Array.Resize(ref slots, newCapacity);
		}
	}
}
