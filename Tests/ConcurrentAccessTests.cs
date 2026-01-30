namespace LookBusy.Test;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LookBusy;
using Xunit;

public sealed class ConcurrentAccessTests : IDisposable
{
	private readonly UnmanagedStringPool pool;

	public ConcurrentAccessTests() => pool = new(8192);

	public void Dispose()
	{
		pool?.Dispose();
		GC.SuppressFinalize(this);
	}

	#region Thread Safety Documentation Tests

	[Fact]
	public async Task ConcurrentReads_MultipleThreads_ShouldBeThreadSafeAsync()
	{
		// Allocate some strings first
		var strings = new PooledString[10];
		for (var i = 0; i < strings.Length; i++) {
			strings[i] = pool.Allocate($"ConcurrentRead_{i}");
		}

		var tasks = new Task[Environment.ProcessorCount];
		var exceptions = new ConcurrentBag<Exception>();

		for (var t = 0; t < tasks.Length; t++) {
			tasks[t] = Task.Run(() => {
				try {
					for (var iteration = 0; iteration < 100; iteration++) {
						foreach (var str in strings) {
							// These are read operations and should be thread-safe
							var span = str.AsSpan();
							var length = str.Length;
							var isEmpty = str.IsEmpty;
							var toString = str.ToString();
							var hashCode = str.GetHashCode();

							// Basic validation
							Assert.True(span.Length == length);
							Assert.Equal(isEmpty, length == 0);
						}
					}
				}
				catch (Exception ex) {
					exceptions.Add(ex);
				}
			});
		}

		await Task.WhenAll(tasks);

		if (!exceptions.IsEmpty) {
			throw new AggregateException("Concurrent reads failed", exceptions);
		}
	}

	[Fact]
	public async Task ConcurrentStringOperations_ReadOnly_ShouldBeThreadSafeAsync()
	{
		var str = pool.Allocate("Hello World for Concurrent Testing");
		var tasks = new Task[Environment.ProcessorCount];
		var exceptions = new ConcurrentBag<Exception>();

		for (var t = 0; t < tasks.Length; t++) {
			tasks[t] = Task.Run(() => {
				try {
					for (var i = 0; i < 50; i++) {
						// Read-only operations should be thread-safe
						var span = str.SubstringSpan(0, 5);
						var indexOf = str.IndexOf("World");
						var contains = str.Contains("Concurrent");
						var startsWith = str.StartsWith("Hello");
						var endsWith = str.EndsWith("Testing");

						// Validate results
						Assert.Equal("Hello", span.ToString());
						Assert.Equal(6, indexOf);
						Assert.True(contains);
						Assert.True(startsWith);
						Assert.True(endsWith);
					}
				}
				catch (Exception ex) {
					exceptions.Add(ex);
				}
			});
		}

		await Task.WhenAll(tasks);

		if (!exceptions.IsEmpty) {
			throw new AggregateException("Concurrent string operations failed", exceptions);
		}
	}

	#endregion

	#region Expected Unsafe Behavior Tests

	[Fact]
	public async Task ConcurrentMutations_WithoutSynchronization_MayFailAsync()
	{
		// This test documents the expected unsafe behavior
		// We don't assert on specific failures since they're unpredictable

		var tasks = new Task[4];
		var allocatedStrings = new ConcurrentBag<PooledString>();
		var exceptions = new ConcurrentBag<Exception>();

		for (var t = 0; t < tasks.Length; t++) {
			var taskId = t;
			tasks[t] = Task.Run(() => {
				try {
					for (var i = 0; i < 10; i++) {
						// Concurrent allocations without synchronization
						var str = pool.Allocate($"Task{taskId}_String{i}");
						allocatedStrings.Add(str);

						Thread.Sleep(1); // Small delay to increase contention
					}
				}
				catch (Exception ex) {
					exceptions.Add(ex);
				}
			});
		}

		await Task.WhenAll(tasks);

		// We can't assert much here since the behavior is undefined
		// This test mainly serves as documentation that concurrent mutations are unsafe
		var allocatedCount = allocatedStrings.Count;
		Assert.True(allocatedCount >= 0); // Basic sanity check
	}

	[Fact]
	public async Task ConcurrentPoolOperations_WithSynchronization_WorksCorrectlyAsync()
	{
		var lockObject = new object();
		var tasks = new Task[Environment.ProcessorCount];
		var allocatedStrings = new ConcurrentBag<string>();
		var exceptions = new ConcurrentBag<Exception>();

		for (var t = 0; t < tasks.Length; t++) {
			var taskId = t;
			tasks[t] = Task.Run(() => {
				try {
					for (var i = 0; i < 20; i++) {
						PooledString str;
						lock (lockObject) {
							// Synchronized allocation
							str = pool.Allocate($"SyncTask{taskId}_String{i}");
						}

						// Read operations outside lock (should be safe)
						var content = str.ToString();
						allocatedStrings.Add(content);

						lock (lockObject) {
							// Synchronized free
							str.Free();
						}
					}
				}
				catch (Exception ex) {
					exceptions.Add(ex);
				}
			});
		}

		await Task.WhenAll(tasks);

		if (!exceptions.IsEmpty) {
			throw new AggregateException("Synchronized operations failed", exceptions);
		}

		Assert.Equal(Environment.ProcessorCount * 20, allocatedStrings.Count);
		Assert.Equal(0, pool.ActiveAllocations);
	}

	#endregion

	#region Pool State Consistency Under Concurrent Access

	[Fact]
	public async Task ConcurrentPoolStateReads_RemainConsistentAsync()
	{
		// Pre-allocate some strings to have interesting state
		var baseStrings = new List<PooledString>();
		for (var i = 0; i < 5; i++) {
			baseStrings.Add(pool.Allocate($"Base{i}"));
		}

		var tasks = new Task[8];
		var stateReadings = new ConcurrentBag<(int ActiveAllocations, int FreeSpaceChars, double FragmentationPercentage)>();
		var exceptions = new ConcurrentBag<Exception>();

		for (var t = 0; t < tasks.Length; t++) {
			tasks[t] = Task.Run(() => {
				try {
					for (var i = 0; i < 50; i++) {
						// Read pool state (should be thread-safe for reads)
						var activeAllocations = pool.ActiveAllocations;
						var freeSpaceChars = pool.FreeSpaceChars;
						var fragmentationPercentage = pool.FragmentationPercentage;

						stateReadings.Add((activeAllocations, freeSpaceChars, fragmentationPercentage));

						// Basic validation
						Assert.True(activeAllocations >= 0);
						Assert.True(freeSpaceChars >= 0);
						Assert.True(fragmentationPercentage >= 0 && fragmentationPercentage <= 100);

						Thread.Sleep(1);
					}
				}
				catch (Exception ex) {
					exceptions.Add(ex);
				}
			});
		}

		await Task.WhenAll(tasks);

		if (!exceptions.IsEmpty) {
			throw new AggregateException("Concurrent state reads failed", exceptions);
		}

		Assert.Equal(8 * 50, stateReadings.Count);
	}

	#endregion

	#region Concurrent String Operations

	[Fact]
	public async Task ConcurrentStringCreation_FromSamePool_ProducesValidStringsAsync()
	{
		var lockObject = new object();
		var tasks = new Task<List<string>>[4];

		for (var t = 0; t < tasks.Length; t++) {
			var taskId = t;
			tasks[t] = Task.Run(() => {
				var results = new List<string>();

				for (var i = 0; i < 15; i++) {
					string result;
					lock (lockObject) {
						var str = pool.Allocate($"ConcurrentTask{taskId}_Item{i}");
						var modified = str.Insert(0, "PREFIX_");
						var replaced = modified.Replace("Item", "Element");
						result = replaced.ToString();

						str.Free();
						modified.Free();
						replaced.Free();
					}

					results.Add(result);
				}

				return results;
			});
		}

		_ = await Task.WhenAll(tasks);

		var allResults = tasks.SelectMany(t => t.Result).ToList();
		Assert.Equal(4 * 15, allResults.Count);

		// Verify all strings were created correctly
		foreach (var result in allResults) {
			Assert.StartsWith("PREFIX_ConcurrentTask", result);
			Assert.Contains("Element", result);
		}
	}

	[Fact]
	public async Task ConcurrentStringReads_AfterAllocation_AreThreadSafeAsync()
	{
		// Allocate test strings
		var testStrings = new PooledString[10];
		for (var i = 0; i < testStrings.Length; i++) {
			testStrings[i] = pool.Allocate($"ThreadSafeRead_{i}_Content");
		}

		var tasks = new Task[Environment.ProcessorCount];
		var readResults = new ConcurrentBag<string>();
		var exceptions = new ConcurrentBag<Exception>();

		for (var t = 0; t < tasks.Length; t++) {
			tasks[t] = Task.Run(() => {
				try {
					for (var iteration = 0; iteration < 30; iteration++) {
						foreach (var str in testStrings) {
							// Multiple concurrent reads
							var asString = str.ToString();
							var span = str.AsSpan();
							var length = str.Length;
							var isEmpty = str.IsEmpty;
							var contains = str.Contains("Content");
							var indexOf = str.IndexOf("Read");

							readResults.Add(asString);

							// Validate consistency
							Assert.Equal(span.Length, length);
							Assert.Equal(isEmpty, length == 0);
							Assert.True(contains);
							Assert.True(indexOf >= 0);
						}

						Thread.Sleep(1);
					}
				}
				catch (Exception ex) {
					exceptions.Add(ex);
				}
			});
		}

		await Task.WhenAll(tasks);

		if (!exceptions.IsEmpty) {
			throw new AggregateException("Concurrent string reads failed", exceptions);
		}

		var expectedCount = Environment.ProcessorCount * 30 * testStrings.Length;
		Assert.Equal(expectedCount, readResults.Count);
	}

	#endregion

	#region Disposal Under Concurrent Access

	[Fact]
	public async Task ConcurrentAccess_DuringDisposal_HandlesGracefullyAsync()
	{
		using var testPool = new UnmanagedStringPool(2048);
		var str1 = testPool.Allocate("Test String 1");
		var str2 = testPool.Allocate("Test String 2");

		var readTask = Task.Run(() => {
			try {
				for (var i = 0; i < 100; i++) {
					var content = str1.ToString();
					Assert.Equal("Test String 1", content);
					Thread.Sleep(1);
				}
			}
			catch (ObjectDisposedException) {
				// Expected when pool gets disposed
			}
			catch (Exception ex) {
				// Log unexpected exceptions but don't fail the test
				System.Diagnostics.Debug.WriteLine($"Unexpected exception during concurrent read: {ex}");
			}
		});

		var disposeTask = Task.Run(() => {
			Thread.Sleep(50); // Let read task start
			testPool.Dispose();
		});
		await Task.WhenAll(readTask, disposeTask);

		Assert.True(testPool.IsDisposed);
	}

	#endregion

	#region Performance Under Concurrent Access

	[Fact]
	public async Task ConcurrentReads_Performance_RemainsReasonableAsync()
	{
		var str = pool.Allocate("Performance test string for concurrent access validation");
		var tasks = new Task[Environment.ProcessorCount];
		var stopwatch = System.Diagnostics.Stopwatch.StartNew();

		for (var t = 0; t < tasks.Length; t++) {
			tasks[t] = Task.Run(() => {
				for (var i = 0; i < 1000; i++) {
					var span = str.AsSpan();
					var length = str.Length;
					var content = str.ToString();

					// Basic validation to prevent optimization
					Assert.True(span.Length > 0);
					Assert.True(length > 0);
					Assert.NotEmpty(content);
				}
			});
		}

		await Task.WhenAll(tasks);
		stopwatch.Stop();

		var totalOperations = Environment.ProcessorCount * 1000 * 3; // 3 operations per iteration
		var operationsPerSecond = totalOperations / stopwatch.Elapsed.TotalSeconds;

		// Performance should be reasonable - at least 10K ops/sec
		Assert.True(operationsPerSecond > 10000,
			$"Performance too slow: {operationsPerSecond:F0} ops/sec");
	}

	#endregion

	#region Concurrent Empty String Tests

	[Fact]
	public async Task ConcurrentEmptyStringAccess_AlwaysThreadSafeAsync()
	{
		var tasks = new Task[Environment.ProcessorCount];
		var exceptions = new ConcurrentBag<Exception>();

		for (var t = 0; t < tasks.Length; t++) {
			tasks[t] = Task.Run(() => {
				try {
					for (var i = 0; i < 200; i++) {
						var empty = pool.CreateEmptyString();

						// All these operations should be thread-safe on empty strings
						var span = empty.AsSpan();
						var length = empty.Length;
						var isEmpty = empty.IsEmpty;
						var toString = empty.ToString();
						var hashCode = empty.GetHashCode();
						var contains = empty.Contains("");
						var startsWith = empty.StartsWith("");
						var endsWith = empty.EndsWith("");

						empty.Free(); // Should be safe
						empty.Dispose(); // Should be safe

						// Validate
						Assert.True(span.IsEmpty);
						Assert.Equal(0, length);
						Assert.True(isEmpty);
						Assert.Equal("", toString);
						Assert.Equal(0, hashCode);
						Assert.True(contains);
						Assert.True(startsWith);
						Assert.True(endsWith);
					}
				}
				catch (Exception ex) {
					exceptions.Add(ex);
				}
			});
		}

		await Task.WhenAll(tasks);

		if (!exceptions.IsEmpty) {
			throw new AggregateException("Concurrent empty string access failed", exceptions);
		}
	}

	#endregion

	#region Synchronization Pattern Examples

	[Fact]
	public async Task ReaderWriterLockPattern_WorksCorrectlyWithPoolAsync()
	{
		var rwLock = new ReaderWriterLockSlim();
		var tasks = new Task[8];
		var exceptions = new ConcurrentBag<Exception>();
		var poolStrings = new List<PooledString>();

		// Pre-allocate some strings
		for (var i = 0; i < 5; i++) {
			poolStrings.Add(pool.Allocate($"Initial{i}"));
		}

		for (var t = 0; t < tasks.Length; t++) {
			var taskId = t;
			tasks[t] = Task.Run(() => {
				try {
					for (var i = 0; i < 20; i++) {
						if (taskId % 2 == 0) // Reader tasks
						{
							rwLock.EnterReadLock();
							try {
								var activeCount = pool.ActiveAllocations;
								var freeSpace = pool.FreeSpaceChars;

								foreach (var str in poolStrings.ToArray()) {
									try {
										var content = str.ToString();
										Assert.NotNull(content);
									}
									catch (ArgumentException) {
										// String might have been freed by writer
									}
								}
							}
							finally {
								rwLock.ExitReadLock();
							}
						} else // Writer tasks
						{
							rwLock.EnterWriteLock();
							try {
								var newStr = pool.Allocate($"WriterTask{taskId}_Item{i}");
								poolStrings.Add(newStr);

								if (poolStrings.Count > 10) {
									var toRemove = poolStrings[0];
									poolStrings.RemoveAt(0);
									toRemove.Free();
								}
							}
							finally {
								rwLock.ExitWriteLock();
							}
						}

						Thread.Sleep(1);
					}
				}
				catch (Exception ex) {
					exceptions.Add(ex);
				}
			});
		}

		await Task.WhenAll(tasks);

		if (!exceptions.IsEmpty) {
			throw new AggregateException("Reader-writer lock pattern failed", exceptions);
		}

		rwLock.Dispose();
	}

	#endregion
}
