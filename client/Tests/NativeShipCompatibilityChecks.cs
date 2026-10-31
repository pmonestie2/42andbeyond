using System;
using System.IO;
using System.Text;
using FortyTwoAndBeyond.Protocol;

internal static class NativeShipCompatibilityChecks
{
    /// <summary>Check full native ship prefixes, inline resources and nested fitted-item effects.</summary>
    public static void Check()
    {
        byte[] body = Wire.Body(writer =>
        {
            writer.Write(1u); writer.Write((ushort)0); writer.Write((byte)1);
            Mask(writer, 58, 2, 3, 4, 5, 6, 7, 36);
            Text(writer, "Captain"); Text(writer, "Explorer");
            writer.Write((byte)3); writer.Write(0xffffffffu); writer.Write(0f); writer.Write(.75f);
            writer.Write(10000f);
            writer.Write((byte)3); writer.Write(0u); writer.Write(0f); writer.Write(.5f);
            writer.Write(20000f);
            Mask(writer, 11, 2, 3, 7, 9);
            Mask(writer, 20, 0, 1); writer.Write(4u); writer.Write(0x80u);
            Mask(writer, 20, 0); Text(writer, "mount_01");
            Mask(writer, 20, 0); Mask(writer, 14, 0, 1, 13);
            writer.Write(2505u); writer.Write(1u);
            writer.Write((byte)3); writer.Write(1500f); writer.Write(1u); writer.Write(2u); writer.Write(3u);
            writer.Write((byte)5);
        });
        ushort size = checked((ushort)(body.Length - 6)); body[4] = (byte)size; body[5] = (byte)(size >> 8);
        var world = new World(); world.Apply(new Frame(0x1b, body), 1);
        if (world.Equipment.Energy != .75f || world.Equipment.MaxEnergy != 10000 ||
            world.Equipment.Shield != .5f || world.Equipment.MaxShield != 20000 ||
            world.Equipment.Mounted[0]?.Template != 2505)
            throw new Exception("Native ship prefixes lost gauges or fitted equipment alignment.");
        byte[] shieldRateOnly = Wire.Body(writer =>
        {
            writer.Write(1u); writer.Write((ushort)14); writer.Write((byte)1);
            Mask(writer, 58, 6); Mask(writer, 3, 0); writer.Write(123456u);
        });
        world.Apply(new Frame(0x1b, shieldRateOnly), 1);
        if (world.Equipment.Shield != .5f || world.Equipment.MaxShield != 20000)
            throw new Exception("A partial resource update erased known shield telemetry.");
        byte[] shieldZero = Wire.Body(writer =>
        {
            writer.Write(1u); writer.Write((ushort)10); writer.Write((byte)1);
            Mask(writer, 58, 6); writer.Write((byte)5);
        });
        world.Apply(new Frame(0x1b, shieldZero), 1);
        if (world.Equipment.Shield != 0 || world.Equipment.MaxShield != 20000)
            throw new Exception("A native resource reset lost capacity instead of setting shield to zero.");
        using (var cached = new LocalSession(System.Threading.CancellationToken.None,
            fallbackItems: new[] { new ItemTemplateInfo(2505, 2, "Cached shield", "", iconId: 911) }))
        {
            cached.World.Apply(new Frame(0x1b, body), 1);
            var snapshot = new Snapshot(cached.World, "ready", true);
            if (snapshot.Mounted[0]?.Name != "Cached shield" || snapshot.Mounted[0].Type != 2 ||
                snapshot.Mounted[0].IconId != 911)
                throw new Exception("A server inventory cache ID lost the bundled item's display metadata.");
        }
        byte[] truncated = new byte[body.Length - 1]; Buffer.BlockCopy(body, 0, truncated, 0, truncated.Length);
        try { new World().Apply(new Frame(0x1b, truncated), 1); }
        catch (InvalidDataException) { Console.WriteLine("PASS: native ship gauges, full prefixes and equipped effect alignment"); return; }
        throw new Exception("Truncated native ship record was accepted.");
    }

    /// <summary>Write the archived single-bit field mask for a synthetic native record.</summary>
    private static void Mask(BinaryWriter writer, int count, params int[] fields)
    {
        var bytes = new byte[(count + 11) / 8]; bytes[0] = 2;
        foreach (int field in fields) bytes[(field + 4) / 8] |= (byte)(1 << ((field + 4) % 8));
        writer.Write(bytes);
    }

    /// <summary>Write one native length-prefixed text field.</summary>
    private static void Text(BinaryWriter writer, string text)
    { byte[] bytes = Encoding.UTF8.GetBytes(text); writer.Write((ushort)bytes.Length); writer.Write(bytes); }
}
