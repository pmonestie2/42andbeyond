using System;
using System.IO;
using System.Linq;
using System.Text;
using FortyTwoAndBeyond.Protocol;

internal static class NativeChatCompatibilityChecks
{
    /// <summary>Verify bounded native channel-control envelopes without weakening chat validation.</summary>
    public static void Check()
    {
        var world = new World();
        foreach (uint kind in new uint[] { 1, 2, 26 })
        {
            byte[] body = Envelope(kind, "Test channel", "Test channel", "", "");
            NativeChatNotice control = NativeChatNotice.Decode(body);
            Require(control.EventType == kind && !control.IsMessage && control.Message.Length == 0 && control.DisplayLine().Length == 0,
                "Native channel-control event was treated as a chat message.");
            world.Apply(new Frame(0xa5, body), 1);
            Require(world.Messages.Count == 0, "Channel-control event polluted chat history.");
            Reject(body.Take(body.Length - 1).ToArray());
            Reject(Envelope(kind, "Test channel", "Other channel", "", ""));
            Reject(Envelope(kind, "Test channel", "Test channel", "", "Unexpected text"));
            Reject(Envelope(kind, "", "", "", ""));
        }
        NativeChatNotice notice = NativeChatNotice.Decode(Envelope(3, "Test sender", "Alias", "Local", "Résumé"));
        Require(notice.IsMessage && notice.DisplayLine() == "[Local] Test sender: Résumé", "Channel-control support changed ordinary Unicode chat.");
        world.Apply(new Frame(0xa5, Envelope(3, "Test sender", "Alias", "Local", "Résumé")), 1);
        Require(world.Messages.Count == 1 && world.Messages.Single() == notice.DisplayLine(), "Ordinary chat no longer reaches history after control events.");
        Reject(Envelope(3, "Test sender", "Alias", "Local", ""));
        Reject(Envelope(255, "Test channel", "Test channel", "", ""));
        byte[] flags = Envelope(1, "Test channel", "Test channel", "", ""); flags[4] = 1; Reject(flags);
        byte[] footer = Envelope(2, "Test channel", "Test channel", "", ""); footer[footer.Length - 1] = 1; Reject(footer);
        byte[] trailing = Envelope(26, "Test channel", "Test channel", "", "").Concat(new byte[] { 0 }).ToArray(); Reject(trailing);
        byte[] malformed = Envelope(3, "A", "A", "Local", "Hello"); malformed[10] = 0xff; Reject(malformed);
        Reject(Envelope(3, "Test sender", "Alias", "Local", "Bad\0text"));
    }

    /// Returns:
    ///     byte[]: A synthetic native envelope, with no captured player or session data.
    private static byte[] Envelope(uint kind, string sender, string alias, string channel, string message) => Wire.Body(writer =>
    {
        writer.Write(kind); writer.Write(0u);
        Text(writer, sender); Text(writer, alias); writer.Write((ushort)0);
        Text(writer, channel); Text(writer, message); writer.Write(0u); writer.Write((ushort)0);
    });

    /// <summary>Encode one native length-prefixed UTF-8 field.</summary>
    private static void Text(BinaryWriter writer, string value)
    { byte[] bytes = Encoding.UTF8.GetBytes(value); writer.Write((ushort)bytes.Length); writer.Write(bytes); }

    /// <summary>Require malformed packets to fail through the protocol validation path.</summary>
    private static void Reject(byte[] body)
    {
        try { NativeChatNotice.Decode(body); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Malformed native chat event was accepted.");
    }

    /// <summary>Fail a concrete compatibility contract violation.</summary>
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
