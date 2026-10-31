using System;
using System.IO;
using System.Text;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>Bounded native chat delivery decoded from the server's 0xa5 frame.</summary>
    public sealed class NativeChatNotice
    {
        public readonly string Sender, Channel, Message;
        public readonly uint EventType;

        /// Returns:
        ///     bool: Whether this event carries displayable chat text rather than channel control data.
        public bool IsMessage => EventType == 3;

        /// <summary>Keep one server-authored chat event immutable.</summary>
        private NativeChatNotice(uint eventType, string sender, string channel, string message)
        { EventType = eventType; Sender = sender; Channel = channel; Message = message; }

        /// <returns>NativeChatNotice: A validated message or supported channel control event with no trailing wire data.</returns>
        public static NativeChatNotice Decode(byte[] body)
        {
            if (body == null || body.Length < 24 || body.Length > 4096 ||
                Wire.U32(body, 4) != 0)
                throw new InvalidDataException("Invalid native chat envelope.");
            uint eventType = Wire.U32(body, 0);
            if (eventType != 3 && eventType != 1 && eventType != 2 && eventType != 26)
                throw new InvalidDataException("Unsupported native chat event type.");
            int offset = 8;
            string sender = ReadText(body, ref offset, 64);
            string alias = ReadText(body, ref offset, 64);
            if (Wire.U16(body, offset) != 0) throw new InvalidDataException("Invalid chat routing field.");
            offset += 2;
            string channel = ReadText(body, ref offset, 64);
            string message = ReadText(body, ref offset, 2048);
            if (body.Length - offset != 6 || Wire.U32(body, offset) != 0 ||
                Wire.U16(body, offset + 4) != 0)
                throw new InvalidDataException("Invalid native chat footer.");
            if (eventType == 3)
            {
                if (sender.Length == 0 || message.Length == 0)
                    throw new InvalidDataException("Invalid native chat message.");
            }
            else if (sender.Length == 0 || alias != sender || channel.Length != 0 || message.Length != 0)
                throw new InvalidDataException("Invalid native chat channel control.");
            return new NativeChatNotice(eventType, sender, channel, message);
        }

        /// <returns>string: One bounded native length-prefixed text field.</returns>
        private static string ReadText(byte[] body, ref int offset, int limit)
        {
            int length = Wire.U16(body, offset); offset += 2;
            if (length > limit) throw new InvalidDataException("Native chat text exceeds limit.");
            Wire.Require(body, offset, length);
            string value;
            try { value = new UTF8Encoding(false, true).GetString(body, offset, length); }
            catch (DecoderFallbackException error)
            { throw new InvalidDataException("Invalid native chat UTF-8.", error); }
            if (value.IndexOf('\0') >= 0) throw new InvalidDataException("Invalid native chat text.");
            offset += length;
            return value;
        }

        /// <returns>string: A compact display line with channel and sender, or empty for a control event.</returns>
        public string DisplayLine()
        { return IsMessage ? "[" + (Channel.Length == 0 ? "Chat" : Channel) + "] " + Sender + ": " + Message : ""; }
    }
}
