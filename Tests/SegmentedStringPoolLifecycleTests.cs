namespace LookBusy.Test;

using System;
using LookBusy;
using Xunit;

public sealed class SegmentedStringPoolLifecycleTests
{
	[Fact]
	public void Clear_InvalidatesAllHandles()
	{
		var pool = new SegmentedStringPool();
		var a = pool.Allocate("hello");
		var b = pool.Allocate("world");
		pool.Clear();
		_ = Assert.Throws<InvalidOperationException>(() => _ = a.AsSpan().Length);
		_ = Assert.Throws<InvalidOperationException>(() => _ = b.AsSpan().Length);
		pool.Dispose();
	}

	[Fact]
	public void Clear_AllowsFreshAllocationsAfterward()
	{
		var pool = new SegmentedStringPool();
		_ = pool.Allocate("hello");
		pool.Clear();
		var after = pool.Allocate("fresh");
		Assert.True(after.AsSpan().SequenceEqual("fresh"));
		pool.Dispose();
	}

	[Fact]
	public void Dispose_InvalidatesHandles_AndSubsequentAllocateThrows()
	{
		var pool = new SegmentedStringPool();
		var h = pool.Allocate("hello");
		pool.Dispose();
		_ = Assert.Throws<ObjectDisposedException>(() => _ = h.AsSpan().Length);
		_ = Assert.Throws<ObjectDisposedException>(() => pool.Allocate("x"));
	}

	[Fact]
	public void Dispose_IsIdempotent()
	{
		var pool = new SegmentedStringPool();
		pool.Dispose();
		pool.Dispose(); // must not throw
	}

	[Fact]
	public void Reserve_AllowsLargeAllocationsWithoutGrowth()
	{
		using var pool = new SegmentedStringPool();
		pool.Reserve(1_000_000);
		for (var i = 0; i < 1000; ++i) {
			_ = pool.Allocate(new string('x', 500));
		}
	}
}
