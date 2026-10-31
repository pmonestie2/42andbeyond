using System;
using System.IO;
using System.Text;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>A native priority/push notice with its original presentation metadata.</summary>
    public sealed class NativePriorityNotice
    {
        public readonly string Message, Destination;
        public readonly uint DurationMs, Priority;

        /// <summary>Keep server-authored notice text and metadata immutable.</summary>
        private NativePriorityNotice(string message, string destination, uint durationMs, uint priority)
        { Message = message; Destination = destination; DurationMs = durationMs; Priority = priority; }

        /// Returns:
        ///     NativePriorityNotice: Two validated native C strings followed by exact signed-nonnegative 32-bit duration and priority fields.
        /// Raises:
        ///     InvalidDataException: Text is malformed, metadata is negative, or the envelope is truncated or has trailing data.
        public static NativePriorityNotice Decode(byte[] body)
        {
            if (body == null || body.Length < 12 || body.Length > 4096)
                throw new InvalidDataException("Invalid native priority notice size.");
            int offset = 0;
            string message = ReadText(body, ref offset, 2048, false);
            string destination = ReadText(body, ref offset, 64, true);
            if (body.Length - offset != 8) throw new InvalidDataException("Invalid native priority notice metadata.");
            uint duration = Wire.U32(body, offset), priority = Wire.U32(body, offset + 4);
            if (duration > int.MaxValue || priority > int.MaxValue)
                throw new InvalidDataException("Negative native priority notice metadata.");
            return new NativePriorityNotice(message, destination, duration, priority);
        }

        /// Returns:
        ///     string: One nonempty bounded UTF-8 C string, preserving printable message whitespace.
        private static string ReadText(byte[] body, ref int offset, int limit, bool destination)
        {
            int end = Array.IndexOf(body, (byte)0, offset);
            if (end < 0 || end == offset || end - offset > limit)
                throw new InvalidDataException("Invalid native priority notice text.");
            string text;
            try { text = new UTF8Encoding(false, true).GetString(body, offset, end - offset); }
            catch (DecoderFallbackException error)
            { throw new InvalidDataException("Invalid native priority notice UTF-8.", error); }
            foreach (char value in text)
                if (char.IsControl(value) && (destination || value != '\n' && value != '\r' && value != '\t'))
                    throw new InvalidDataException("Invalid native priority notice control character.");
            offset = end + 1; return text;
        }
    }
}
