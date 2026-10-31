using System;
using System.IO;
using FortyTwoAndBeyond.Protocol;

internal static class GlobalAuxPrefixChecks
{
    /// <summary>Exercise the archived PlayerIndex field order before journal and reputation.</summary>
    public static void Check()
    {
        byte[] body = FullPlayerAux();
        var world = new World();
        world.Apply(new Frame(0x1b, body), 1);
        if (world.Missions.Length != 12 || world.Reputation == null ||
            world.Experience?.CombatLevel != 2 || world.Experience.AvailableSkillPoints != 3 ||
            world.Skills.Length != 0 || world.AvailableAbilities.Length != 0 ||
            world.Vendor[0]?.Template != 2505)
            throw new InvalidDataException("Full native player prefix was not decoded in field order.");
        byte[] corrupt = (byte[])body.Clone();
        // Secure inventory's nested empty mask is a typed 0x05 sentinel.
        corrupt[22] = 4;
        try { new World().Apply(new Frame(0x1b, corrupt), 1); }
        catch (InvalidDataException) { Console.WriteLine("PASS: full PlayerIndex prefix and strict nested reset"); return; }
        throw new InvalidDataException("Malformed PlayerIndex nested reset was accepted.");
    }

    /// Returns:
    ///     byte[]: A full archived PlayerIndex shaped AUX body with typed item and RPG prefixes.
    private static byte[] FullPlayerAux()
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(0u); writer.Write((ushort)0); writer.Write((byte)1);
            writer.Write(Mask(18, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10));
            writer.Write(900UL); writer.Write(2u); // Credits, XP debt.
            writer.Write((byte)5); // Secure inventory.
            writer.Write(Mask(128, 0)); // One vendor item, then empty reward and overflow.
            writer.Write(Mask(10, 0, 3, 7, 8));
            writer.Write(2505u); writer.Write(3.5f);
            Text(writer, "Activated"); Text(writer, "Equipped");
            writer.Write((byte)5); writer.Write((byte)5);
            writer.Write(Mask(16, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15));
            writer.Write(1u); writer.Write(2u); // Race, profession.
            writer.Write((byte)5); writer.Write((byte)5); // Skills and abilities.
            writer.Write(3u); writer.Write(4u);
            writer.Write(.5f); writer.Write(2u); writer.Write(.25f); writer.Write(1u);
            writer.Write(.75f); writer.Write(3u); writer.Write(0u);
            writer.Write(100u); writer.Write(400u); writer.Write(89u);
            Text(writer, "EVT"); writer.Write(7u); // Event flags, music.
            writer.Write((byte)5); writer.Write((byte)5); // Missions, reputation.
            byte[] body = stream.ToArray();
            BitConverter.GetBytes(checked((ushort)(body.Length - 6))).CopyTo(body, 4);
            return body;
        }
    }

    /// Returns:
    ///     byte[]: Native one-bit AUX mask for the selected fields.
    private static byte[] Mask(int count, params int[] present)
    {
        var bytes = new byte[(4 + count + 7) / 8]; bytes[0] = 2;
        foreach (int field in present) bytes[(4 + field) / 8] |= (byte)(1 << ((4 + field) % 8));
        return bytes;
    }

    /// <summary>Write one native length-prefixed string.</summary>
    private static void Text(BinaryWriter writer, string value)
    {
        byte[] encoded = System.Text.Encoding.UTF8.GetBytes(value);
        writer.Write(checked((ushort)encoded.Length)); writer.Write(encoded);
    }
}
