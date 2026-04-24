namespace LookBusy.Test;

using System;
using System.Buffers;
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

// Task 9: AsSpan, Length, Dispose
public sealed class PooledStringRefRoundtripTests : IDisposable
{
	private readonly SegmentedStringPool pool = new();

	public void Dispose()
	{
		pool.Dispose();
		GC.SuppressFinalize(this);
	}

	[Fact]
	public void AsSpan_SmallString_RoundTrips()
	{
		var r = pool.Allocate("hello");
		Assert.True(r.AsSpan().SequenceEqual("hello"));
	}

	[Fact]
	public void AsSpan_LargeString_RoundTrips()
	{
		var big = new string('q', 300);
		var r = pool.Allocate(big);
		Assert.True(r.AsSpan().SequenceEqual(big));
	}

	[Fact]
	public void AsSpan_Empty_ReturnsEmpty()
	{
		Assert.True(PooledStringRef.Empty.AsSpan().IsEmpty);
	}

	[Fact]
	public void Length_ReturnsChars()
	{
		var r = pool.Allocate("hello");
		Assert.Equal(5, r.Length);
	}

	[Fact]
	public void Length_EmptyRef_ReturnsZero()
	{
		Assert.Equal(0, PooledStringRef.Empty.Length);
	}

	[Fact]
	public void Dispose_FreesAllocation()
	{
		var r = pool.Allocate("hello");
		r.Dispose();
		Assert.Equal(0, pool.ActiveAllocations);
	}
}

// Task 10: query methods
public sealed class PooledStringRefQueryTests : IDisposable
{
	private readonly SegmentedStringPool pool = new();

	public void Dispose() { pool.Dispose(); GC.SuppressFinalize(this); }

	[Fact]
	public void IndexOf_Found_ReturnsOffset()
	{
		var r = pool.Allocate("hello world");
		Assert.Equal(6, r.IndexOf("world"));
	}

	[Fact]
	public void IndexOf_NotFound_ReturnsMinusOne()
	{
		var r = pool.Allocate("hello");
		Assert.Equal(-1, r.IndexOf("xyz"));
	}

	[Fact]
	public void LastIndexOf_Found_ReturnsLastOffset()
	{
		var r = pool.Allocate("a.b.c");
		Assert.Equal(3, r.LastIndexOf("."));
	}

	[Fact]
	public void StartsWith_True_WhenPrefixMatches()
	{
		var r = pool.Allocate("hello world");
		Assert.True(r.StartsWith("hello"));
		Assert.False(r.StartsWith("world"));
	}

	[Fact]
	public void EndsWith_True_WhenSuffixMatches()
	{
		var r = pool.Allocate("hello world");
		Assert.True(r.EndsWith("world"));
		Assert.False(r.EndsWith("hello"));
	}

	[Fact]
	public void Contains_True_WhenSubstringPresent()
	{
		var r = pool.Allocate("hello world");
		Assert.True(r.Contains("llo wo"));
	}

	[Fact]
	public void SubstringSpan_ReturnsSlice()
	{
		var r = pool.Allocate("hello world");
		Assert.True(r.SubstringSpan(6, 5).SequenceEqual("world"));
	}

	[Fact]
	public void SubstringSpan_OutOfRange_Throws()
	{
		var r = pool.Allocate("hi");
		_ = Assert.Throws<ArgumentOutOfRangeException>(() => r.SubstringSpan(5, 1));
	}

	[Fact]
	public void EmptyRef_QueryMethods_ReturnConventionalResults()
	{
		var e = PooledStringRef.Empty;
		Assert.Equal(-1, e.IndexOf("x"));
		Assert.Equal(0, e.IndexOf(""));
		Assert.True(e.StartsWith(""));
		Assert.False(e.StartsWith("x"));
	}
}

// Task 11: Duplicate
public sealed class PooledStringRefDuplicateTests : IDisposable
{
	private readonly SegmentedStringPool pool = new();

	public void Dispose() { pool.Dispose(); GC.SuppressFinalize(this); }

	[Fact]
	public void Duplicate_ProducesEqualButDistinctHandle()
	{
		var a = pool.Allocate("hello");
		var b = a.Duplicate();
		Assert.True(a.AsSpan().SequenceEqual(b.AsSpan()));
		Assert.NotEqual(a.SlotIndex, b.SlotIndex);
	}

	[Fact]
	public void Duplicate_FreeingOneDoesNotAffectOther()
	{
		var a = pool.Allocate("hello");
		var b = a.Duplicate();
		a.Free();
		Assert.True(b.AsSpan().SequenceEqual("hello"));
	}

	[Fact]
	public void Duplicate_OfEmpty_IsEmpty()
	{
		var b = PooledStringRef.Empty.Duplicate();
		Assert.True(b.IsEmpty);
		Assert.Equal(0, b.Length);
	}
}

// Task 12: Insert
public sealed class PooledStringRefInsertTests : IDisposable
{
	private readonly SegmentedStringPool pool = new();

	public void Dispose() { pool.Dispose(); GC.SuppressFinalize(this); }

	[Fact]
	public void Insert_AtBeginning_PrependsValue()
	{
		var r = pool.Allocate("world");
		var result = r.Insert(0, "hello ");
		Assert.True(result.AsSpan().SequenceEqual("hello world"));
	}

	[Fact]
	public void Insert_AtEnd_AppendsValue()
	{
		var r = pool.Allocate("hello");
		var result = r.Insert(5, " world");
		Assert.True(result.AsSpan().SequenceEqual("hello world"));
	}

	[Fact]
	public void Insert_InMiddle_InsertsValue()
	{
		var r = pool.Allocate("held");
		var result = r.Insert(2, "xyz");
		Assert.True(result.AsSpan().SequenceEqual("hexyzld"));
	}

	[Fact]
	public void Insert_DoesNotMutateOriginal()
	{
		var r = pool.Allocate("abc");
		_ = r.Insert(1, "XY");
		Assert.True(r.AsSpan().SequenceEqual("abc"));
	}

	[Fact]
	public void Insert_IntoEmpty_AtZero_ReturnsEmpty()
	{
		var result = PooledStringRef.Empty.Insert(0, "x");
		Assert.True(result.IsEmpty);
	}

	[Fact]
	public void Insert_IndexOutOfRange_Throws()
	{
		var r = pool.Allocate("abc");
		_ = Assert.Throws<ArgumentOutOfRangeException>(() => r.Insert(4, "x"));
		_ = Assert.Throws<ArgumentOutOfRangeException>(() => r.Insert(-1, "x"));
	}
}

// Task 13: Replace
public sealed class PooledStringRefReplaceTests : IDisposable
{
	private readonly SegmentedStringPool pool = new();

	public void Dispose() { pool.Dispose(); GC.SuppressFinalize(this); }

	[Fact]
	public void Replace_Single_ReplacesOnce()
	{
		var r = pool.Allocate("hello world");
		var result = r.Replace("world", "everyone");
		Assert.True(result.AsSpan().SequenceEqual("hello everyone"));
	}

	[Fact]
	public void Replace_Multiple_NonOverlapping()
	{
		var r = pool.Allocate("a-b-c-d");
		var result = r.Replace("-", "::");
		Assert.True(result.AsSpan().SequenceEqual("a::b::c::d"));
	}

	[Fact]
	public void Replace_NoMatches_ReturnsEqualContent()
	{
		var r = pool.Allocate("hello");
		var result = r.Replace("xyz", "abc");
		Assert.True(result.AsSpan().SequenceEqual("hello"));
	}

	[Fact]
	public void Replace_WithEmpty_RemovesMatches()
	{
		var r = pool.Allocate("aXbXc");
		var result = r.Replace("X", "");
		Assert.True(result.AsSpan().SequenceEqual("abc"));
	}

	[Fact]
	public void Replace_LargeInput_OverflowsStackallocPath()
	{
		var src = new string('x', 200).Replace("xx", "xX", StringComparison.Ordinal);
		var r = pool.Allocate(src);
		var result = r.Replace("X", "Y");
		Assert.Equal(src.Replace("X", "Y", StringComparison.Ordinal), result.AsSpan().ToString());
	}

	[Fact]
	public void Replace_EmptyOldValue_Throws()
	{
		var r = pool.Allocate("abc");
		_ = Assert.Throws<ArgumentException>(() => r.Replace("", "x"));
	}

	// P0-5: verify the heap-rented paths (totalLength > 256) complete without leaking.
	// The direct mid-Allocate throw scenario requires concurrent pool.Dispose(), which violates
	// the single-threaded contract; we validate the structural fix via the happy path and
	// that exceptions reaching us (from argument errors) are not swallowed.
	[Fact]
	public void Insert_LargeString_HeapRentedPath_RoundTrips()
	{
		var src = new string('a', 200);
		var ins = new string('b', 100); // total 300 > 256 → forces ArrayPool rent
		var r = pool.Allocate(src);
		var result = r.Insert(100, ins.AsSpan());
		Assert.Equal(src.Insert(100, ins), result.AsSpan().ToString());
	}

	[Fact]
	public void Replace_ManyMatches_OverflowsInlineMatchBuffer()
	{
		// 65 matches exceeds the 64-entry inline stackalloc, forcing rentedMatches ArrayPool path
		var src = string.Concat(Enumerable.Repeat("x|", 65));
		var r = pool.Allocate(src);
		var result = r.Replace("|", "-");
		Assert.Equal(src.Replace("|", "-", StringComparison.Ordinal), result.AsSpan().ToString());
	}
}

// Task 14: Equals / GetHashCode / ToString
public sealed class PooledStringRefEqualityTests : IDisposable
{
	private readonly SegmentedStringPool pool = new();

	public void Dispose() { pool.Dispose(); GC.SuppressFinalize(this); }

	[Fact]
	public void Equals_SameContent_DifferentSlots_IsTrue()
	{
		var a = pool.Allocate("hello");
		var b = pool.Allocate("hello");
		Assert.True(a.Equals(b));
		Assert.Equal(a.GetHashCode(), b.GetHashCode());
	}

	[Fact]
	public void Equals_DifferentContent_IsFalse()
	{
		var a = pool.Allocate("hello");
		var b = pool.Allocate("world");
		Assert.False(a.Equals(b));
	}

	[Fact]
	public void Equals_EmptyAndEmpty_IsTrue()
	{
		Assert.True(PooledStringRef.Empty.Equals(PooledStringRef.Empty));
	}

	[Fact]
	public void Equals_Object_CompatibleWithString()
	{
		var a = pool.Allocate("hello");
		Assert.True(a.Equals((object)"hello"));
		Assert.False(a.Equals((object)"HELLO"));
	}

	[Fact]
	public void ToString_ReturnsManagedCopy()
	{
		var a = pool.Allocate("hello");
		var s = a.ToString();
		Assert.Equal("hello", s);
	}

	[Fact]
	public void GetHashCode_LongStrings_DifferByContent()
	{
		var a = pool.Allocate(new string('a', 500));
		var b = pool.Allocate(new string('b', 500));
		Assert.NotEqual(a.GetHashCode(), b.GetHashCode());
	}

	[Fact]
	public void OperatorEquals_SameContent_IsTrue()
	{
		var a = pool.Allocate("abc");
		var b = pool.Allocate("abc");
		Assert.True(a == b);
		Assert.False(a != b);
	}
}
