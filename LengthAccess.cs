namespace Playground;

/* Should we cache array.Length in a tight loop? No! The generated assembly code is highly optimized by the compiler.

   x64 assembly analysis for simplified code https://godbolt.org/z/fnxP5xMjc
 
	var sum = 0;
	var arr = new int[100];
	for(var i = 0; i < arr.Length; ++i)		// accessing the Length property in every iteration?
		sum += arr[i];
	Console.WriteLine(sum);
 
COMPILES TO:
 
	Program:Main() (FullOpts):
		push     rbp
		push     rbx
		push     rax
		lea      rbp, [rsp+0x10]
		xor      ebx, ebx

		mov      rdi, 0x7C07E9638A98					; int[]
		mov      esi, 100								; array size
		call     CORINFO_HELP_NEWARR_1_VC				; create new int[100]
		add      rax, 16								; point to first element
		mov      edi, 100								; array size, loop counter goes DOWN from 100, while edi > 0.
		align    [0 bytes for IG03]						; padding for loop

	G_FOR_LOOP:  ;; offset=0x0027						; for loop start
		add      ebx, dword ptr [rax]					; add arr[i] to sum
		add      rax, 4									; move forward to next element
		dec      edi									; decrement loop counter		
		jne      SHORT G_FOR_LOOP						; continue loop if edi not zero -- jump to G_FOR_LOOP

		mov      edi, ebx
		add      rsp, 8
		pop      rbx
		pop      rbp
		tail.jmp [System.Console:WriteLine(int)]

Note the generated assembly uses immediate value for the loop counter (edi register), and counts DOWN even though the loop
is counting up (rax register). This is optimal because checking for zero is cheaper than checking for 100.
Even if the array size was defined at runtime, the only change would be to load the value into edi once before the loop.

Register values run like this:

	RAX - pointer to the current element in the array
	EDI - loop counter, runs down from 100 to 0

	RAX		EDI
	=================
	0		99
	1		98
	2		97
	...
	98		2
	99		1
	100		0 (end of loop)

CONCLUSION:

There is no performance advantage in caching the array length in a variable, its not idiomatic and lacks clarity.

*/

using System;
using System.Diagnostics;

/* Empirical test of array.Length compared to caching the value, and accessing it indirectly through another property.
 * 
 * .NET 9.0.301 on x64 (June 2025)
 * 
 * dotnet build -c Release
 * 
 * My Results on old laptop (higher is faster):
 * 
 * Length in loop    - Loops per second: 37573423.8 (3rd)
 * Cached property   - Loops per second: 38146100.2 (1st)
 * Indirect property - Loops per second: 37725391.0 (2nd)

 * Length in loop    - Loops per second: 38066571.0 (1st)
 * Cached property   - Loops per second: 36347233.6 (3rd)
 * Indirect property - Loops per second: 37019500.4 (2nd)
 */

internal static class LengthAccess
{
	private static readonly TimeSpan DurationToRun = TimeSpan.FromSeconds(5);
	private static readonly int[] numbers = new int[10_000];

	/// <summary>
	/// Another level of indirection
	/// </summary>
	private static int LengthProperty => numbers.Length;

	public static void Run()
	{
		// Initial setup, not timed
		for (var i = 0; i < numbers.Length; i++) {
			numbers[i] = i;
		}
		Console.WriteLine("Running Length Access Benchmark...");

		// warm up the memory cache
		CachedLengthProperty(true);

		// run the tests 3 times
		for (var i = 0; i < 3; i++) {
			UseLengthProperty();
			CachedLengthProperty();
			IndirectLengthProperty();
		}
	}

	private static void UseLengthProperty()
	{
		// timing accessing Length property, loop for 5 seconds
		var index = 0;
		var count = 0L;
		var sum = 0L;
		var sw = Stopwatch.StartNew();
		while (sw.Elapsed < DurationToRun) {
			sum += numbers[index];
			index++;
			count++;
			if (index >= numbers.Length) {  // vanilla Length access
				index = 0;
			}
		}
		sw.Stop();
		var loopsPerSecond = count / sw.Elapsed.TotalSeconds;

		Console.WriteLine($"Length in loop    - Loops per second: {loopsPerSecond:f1}");
	}

	private static void CachedLengthProperty(bool warmup = false)
	{
		var cachedLength = numbers.Length; // Cache the Length property
		var index = 0;
		var count = 0L;
		var sum = 0L;
		var sw = Stopwatch.StartNew();
		while (sw.Elapsed < DurationToRun) {
			sum += numbers[index];
			index++;
			count++;
			if (index >= cachedLength) { // Use the cached length
				index = 0;
			}
		}
		sw.Stop();
		var loopsPerSecond = count / sw.Elapsed.TotalSeconds;

		if (warmup) {
			Console.WriteLine("Warmup completed");
		} else {
			Console.WriteLine($"Cached property   - Loops per second: {loopsPerSecond:f1}");
		}
	}

	private static void IndirectLengthProperty()
	{
		var index = 0;
		var count = 0L;
		var sum = 0L;
		var sw = Stopwatch.StartNew();
		while (sw.Elapsed < DurationToRun) {
			sum += numbers[index];
			index++;
			count++;
			if (index >= LengthProperty) {  // Indirect access to Length property
				index = 0;
			}
		}
		sw.Stop();
		var loopsPerSecond = count / sw.Elapsed.TotalSeconds;

		Console.WriteLine($"Indirect property - Loops per second: {loopsPerSecond:f1}");
	}
}
