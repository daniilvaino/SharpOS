using System;
using System.Collections.Generic;

namespace XtermSharp {
	/// <summary>
	/// Buffer for processing input
	/// </summary>
	/// <remarks>
	/// Because data might not be complete, we need to put back data that we read to process on
	/// a future read.  To prepare for reading, on every call to parse, the prepare method is
	/// given the new buffer to read from.
	///
	/// the `hasNext` describes whether there is more data left on the buffer, and `bytesLeft`
	/// returnes the number of bytes left.   The `getNext` method fetches either the next
	/// value from the putback buffer, or when it is empty, it returns it from the buffer that
	/// was passed during prepare.
	///
	/// Additionally, the terminal parser needs to reset the parser state on demand, and
	/// that is surfaced via reset
	/// </remarks>
	class ReadingBuffer {
		// SharpOS change: one shared empty array instead of a fresh `new
		// byte[0]` at each of the three sites below.
		//
		// Done() runs at the end of every parse pass, so on a terminal that is
		// printing this allocated once per write. Harmless on a runtime with a
		// generational collector; here nothing collects the kernel heap unless
		// an allocation fails, and this alone put 1.45 million byte[] and
		// 49 MiB into it in under a minute of PowerShell output — the machine
		// then slowed to a standstill. Array.Empty<T>() is no help: ours
		// allocates too.
		static readonly byte[] s_empty = new byte [0];

		byte[] putbackBuffer = s_empty;
		unsafe byte* buffer;
		int bufferStart;
		int totalCount;
		int index;

		unsafe public void Prepare (byte* data, int start, int length)
		{
			buffer = data;
			bufferStart = start;

			index = 0;
			totalCount = putbackBuffer.Length + length;
		}

		public int BytesLeft ()
		{
			return totalCount - index;
		}

		public bool HasNext ()
		{
			return index < totalCount;
		}

		unsafe public byte GetNext ()
		{
			byte val;
			if (index < putbackBuffer.Length) {
				// grab from putback buffer
				val = putbackBuffer [index];
			} else {
				// grab from the prepared buffer
				val = buffer [bufferStart + (index - putbackBuffer.Length)];
			}

			index++;
			return val;
		}

		/// <summary>
		/// Puts back code and the remainder of the buffer
		/// </summary>
		public void Putback (byte code)
		{
			var left = BytesLeft ();
			byte [] newPutback = new byte[left + 1];
			newPutback [0] = code;

			for (int i = 0; i < left; i++) {
				newPutback [i + 1] = GetNext ();
			}

			putbackBuffer = newPutback;
		}

		unsafe public void Done ()
		{
			if (index < putbackBuffer.Length) {
				byte [] newPutback = new byte [putbackBuffer.Length - index];
				Array.Copy (putbackBuffer, index, newPutback, 0, newPutback.Length);
				putbackBuffer = newPutback;
			} else {
				putbackBuffer = s_empty;                // SharpOS change: see s_empty
			}

			buffer = null;
		}

		public void Reset ()
		{
			putbackBuffer = s_empty;                // SharpOS change: see s_empty
			index = 0;
		}
	}
}
