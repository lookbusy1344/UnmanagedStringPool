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
		for (var i = 0; i < 4; ++i) {
			_ = tier.Allocate(5, out _);
		}
		_ = tier.Allocate(5, out _);
		Assert.True(tier.SlabCount >= 2);
	}

	[Fact]
	public void Free_ReturnsCellToSlab()
	{
		var ptr = tier.Allocate(5, out var slab);
		tier.Free(ptr, slab);
		Assert.Equal(4, slab.FreeCells);
	}

	[Fact]
	public void Allocate_FillingSlab_DetachesItFromActiveChain()
	{
		SegmentedSlab? slabA = null;
		for (var i = 0; i < 4; ++i) {
			_ = tier.Allocate(5, out var s);
			slabA = s;
		}
		Assert.NotNull(slabA);
		Assert.True(slabA!.IsFull);

		_ = tier.Allocate(5, out var slabB);
		Assert.NotSame(slabA, slabB);
		Assert.False(slabB.IsFull);
	}

	[Fact]
	public void Free_OnFullSlab_RelinksItToActiveChainHead()
	{
		var firstPtr = tier.Allocate(5, out var slabA);
		for (var i = 1; i < 4; ++i) {
			_ = tier.Allocate(5, out _);
		}
		Assert.True(slabA.IsFull);

		_ = tier.Allocate(5, out var slabB);
		Assert.NotSame(slabA, slabB);

		tier.Free(firstPtr, slabA);

		_ = tier.Allocate(5, out var slabAfterFree);
		Assert.Same(slabA, slabAfterFree);
	}

	[Fact]
	public void Free_OnNonFullSlab_DoesNotDuplicateChainEntry()
	{
		var p1 = tier.Allocate(5, out var slabA);
		var p2 = tier.Allocate(5, out _);
		tier.Free(p1, slabA);
		tier.Free(p2, slabA);

		// Slab still has all cells free; allocate 4 more should reuse the same slab,
		// not silently spawn duplicates from a corrupt chain.
		for (var i = 0; i < 4; ++i) {
			_ = tier.Allocate(5, out var s);
			Assert.Same(slabA, s);
		}
		Assert.Equal(1, tier.SlabCount);
	}

	[Fact]
	public void ResetAll_RebuildsChainAcrossAllSlabs()
	{
		// Fill slab A so it gets detached.
		SegmentedSlab? slabA = null;
		for (var i = 0; i < 4; ++i) {
			_ = tier.Allocate(5, out var s);
			slabA = s;
		}
		_ = tier.Allocate(5, out var slabB);
		Assert.True(slabA!.IsFull);

		tier.ResetAll();

		// After reset, every existing slab should be on the chain — fill the head and
		// confirm we land in the second slab without spawning a third.
		var slabCountBefore = tier.SlabCount;
		for (var i = 0; i < 4; ++i) {
			_ = tier.Allocate(5, out _);
		}
		_ = tier.Allocate(5, out _);
		Assert.Equal(slabCountBefore, tier.SlabCount);
	}

	// P1-3: inject a full slab back onto the active chain (simulating a chain-head invariant
	// violation) and confirm the recovery path detaches it rather than leaking it into position 2.
	[Fact]
	public void Allocate_FullChainHead_RecoveryDetachesFullSlabAndSucceeds()
	{
		// Fill slab0 completely so it gets detached.
		SegmentedSlab? slab0 = null;
		for (var i = 0; i < 4; ++i) {
			_ = tier.Allocate(5, out slab0);
		}
		Assert.True(slab0!.IsFull);
		var slabCountAfterFill = tier.SlabCount;

		// Inject the full slab back at the chain head — simulates the invariant violation.
		tier.TestOnly_InjectAtChainHead(0, slab0);

		// Recovery path: Allocate must detach the full head and succeed from a fresh slab.
		var ptr = tier.Allocate(5, out var slab1);
		Assert.NotEqual(IntPtr.Zero, ptr);
		Assert.NotSame(slab0, slab1);

		// A second allocation must not hit slab0 again (chain is clean).
		_ = tier.Allocate(5, out var slab2);
		Assert.NotSame(slab0, slab2);

		// One extra slab allocated during recovery; slab0 removed from the active chain
		// so subsequent allocations come from the fresh slab, not the injected full one.
		Assert.True(tier.SlabCount > slabCountAfterFill);
	}
}
