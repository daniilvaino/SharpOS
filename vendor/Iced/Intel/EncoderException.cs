// SPDX-License-Identifier: MIT
// Copyright (C) 2018-present iced project and contributors

#if ENCODER
using System;
// SharpOS: [Serializable] + SerializationInfo ctor removed. Legacy
// BinaryFormatter surface; our Exception has no (SerializationInfo,
// StreamingContext) ctor to chain through. Other ports drop the same.

namespace Iced.Intel {
	/// <summary>
	/// Thrown if the encoder can't encode an instruction
	/// </summary>
	public class EncoderException : Exception {
		/// <summary>
		/// The instruction that couldn't be encoded
		/// </summary>
		public Instruction Instruction { get; }

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="message">Exception message</param>
		/// <param name="instruction">Instruction</param>
		public EncoderException(string message, in Instruction instruction) : base(message) => Instruction = instruction;
	}
}
#endif
