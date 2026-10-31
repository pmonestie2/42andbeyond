using System;
using System.IO;
using FortyTwoAndBeyond.Protocol;

internal static class NativeMountCapacityChecks
{
    /// <summary>Check future mount totals never replace the native currently unlocked masks.</summary>
    public static void Check()
    {
        var world = new World();
        world.Apply(new Frame(0x1b, Inventory(5, 4, new[] { 3, 9 })), 1);
        var first = new Snapshot(world, "ready", true);
        if (first.WeaponMounts != 1 || first.DeviceMounts != 1)
            throw new Exception("Future mount totals were presented as currently unlocked slots.");
        world.Apply(new Frame(0x1b, Inventory(5, 4, new[] { 4 }, includeFuture: false)), 1);
        var second = new Snapshot(world, "ready", true);
        if (second.WeaponMounts != 2 || second.DeviceMounts != 1 || first.WeaponMounts != 1)
            throw new Exception("Partial mount revision lost earlier sockets or changed a snapshot.");
        world.Apply(new Frame(0x1b, InventoryReset()), 1);
        if (world.Equipment.WeaponMounts != 0 || world.Equipment.DeviceMounts != 0)
            throw new Exception("Native mount reset retained unlocked sockets.");
        byte[] credits = Wire.Body(writer =>
        { writer.Write(0u); writer.Write((ushort)0); writer.Write((byte)1);
            Mask(writer, 18, 0); writer.Write(187u); writer.Write(0u); });
        Seal(credits);
        world.Apply(new Frame(0x1b, credits), 1);
        if (new Snapshot(world, "ready", true).Credits != 187)
            throw new Exception("Inventory credit balance did not follow global player AUX.");
        Console.WriteLine("PASS: native unlocked mount masks, partial revisions and inventory credits");
    }

    /// <summary>
    /// Returns:
    ///     byte[]: A native inventory revision with selected hardpoint masks and optional future totals.
    /// </summary>
    private static byte[] Inventory(uint futureWeapons, uint futureDevices, int[] mounts,
        bool includeFuture = true)
    {
        byte[] body = Wire.Body(writer =>
        {
            writer.Write(1u); writer.Write((ushort)0); writer.Write((byte)1);
            Mask(writer, 58, 36);
            if (includeFuture) Mask(writer, 11, 2, 4, 5);
            else Mask(writer, 11, 2);
            Mask(writer, 20, mounts);
            foreach (int slot in mounts)
            {
                writer.Write(slot < 9 ? 0x1c002u : 0x20809u);
                writer.Write(uint.MaxValue); writer.Write(uint.MaxValue);
            }
            if (includeFuture) { writer.Write(futureWeapons); writer.Write(futureDevices); }
        });
        Seal(body); return body;
    }

    /// <summary>
    /// Returns:
    ///     byte[]: A native reset of the inventory mount-layout record.
    /// </summary>
    private static byte[] InventoryReset()
    {
        byte[] body = Wire.Body(writer =>
        { writer.Write(1u); writer.Write((ushort)0); writer.Write((byte)1);
            Mask(writer, 58, 36); Mask(writer, 11, 2); writer.Write((byte)5); });
        Seal(body); return body;
    }

    /// <summary>Write a native one-bit field mask.</summary>
    private static void Mask(BinaryWriter writer, int count, params int[] fields)
    {
        byte[] flags = new byte[(count + 11) / 8]; flags[0] = 2;
        foreach (int field in fields)
            flags[(field + 4) / 8] |= (byte)(1 << ((field + 4) % 8));
        writer.Write(flags);
    }

    /// <summary>Fill the native AUX envelope length after its fields are written.</summary>
    private static void Seal(byte[] body)
    { ushort length = checked((ushort)(body.Length - 6));
        body[4] = (byte)length; body[5] = (byte)(length >> 8); }
}
