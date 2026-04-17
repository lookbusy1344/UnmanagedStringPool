namespace LookBusy.Test;

using System;
using LookBusy;
using Xunit;

public sealed class PooledStringRefTests
{
	[Fact]
	public void Empty_DefaultValue_IsEmpty()
	{
		var r = default(PooledStringRef);
		Assert.True(r.IsEmpty);
	}

	[Fact]
	public void Empty_StaticProperty_ReturnsDefault()
	{
		var a = PooledStringRef.Empty;
		var b = default(PooledStringRef);
		Assert.Equal(a, b);
	}

	[Fact]
	public void Empty_HasNullPoolAndZeroHandle()
	{
		var r = PooledStringRef.Empty;
		Assert.Null(r.Pool);
		Assert.Equal(0u, r.SlotIndex);
		Assert.Equal(0u, r.Generation);
	}
}
