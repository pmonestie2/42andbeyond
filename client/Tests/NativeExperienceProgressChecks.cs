using System;
using System.IO;
using FortyTwoAndBeyond.Protocol;

internal static class NativeExperienceProgressChecks
{
    /// <summary>Verify native progress tracks, level-up resets and cross-sector persistence.</summary>
    public static void Check()
    {
        var world = new World(1);
        world.Apply(new Frame(0x1b, Progress()), 1);
        var before = new Snapshot(world, "ready", true);
        if (before.Experience.CombatLevel != 1 || before.Experience.CombatFraction != .4f ||
            before.Experience.ExploreLevel != 3 || before.Experience.ExploreFraction != .3f ||
            before.Experience.TradeLevel != 2 || before.Experience.TradeFraction != .25f)
            throw new Exception("Native combat, exploration and trade fields were interchanged.");
        var transferred = new World(2);
        transferred.InheritCharacterProgress(world);
        transferred.Apply(new Frame(0x1b, Progress(levelUp: true)), 1);
        var after = new Snapshot(transferred, "ready", true);
        if (after.Experience.CombatLevel != 2 || after.Experience.CombatFraction != 0 ||
            after.Experience.TradeFraction != .25f || after.Experience.ExploreFraction != .3f ||
            before.Experience.CombatLevel != 1 || before.Experience.CombatFraction != .4f)
            throw new Exception("Level-up reset lost another XP track or changed an earlier snapshot.");
        transferred.Apply(new Frame(0x1b, Reset()), 1);
        if (transferred.Experience.CombatLevel != 0 || transferred.Experience.TradeLevel != 0 ||
            transferred.Experience.ExploreFraction != 0 || after.Experience.CombatLevel != 2)
            throw new Exception("A native RPG reset retained XP or changed the preceding snapshot.");
        Console.WriteLine("PASS: native XP tracks, zero progress on level-up and sector inheritance");
    }

    /// Returns:
    ///     byte[]: An explicit native reset of the RPG container.
    private static byte[] Reset()
    {
        byte[] body = Wire.Body(writer =>
        {
            writer.Write(0u); writer.Write((ushort)0); writer.Write((byte)1);
            Mask(writer, 18, Array.Empty<int>(), new[] { 6 }); writer.Write((byte)5);
        });
        ushort length = checked((ushort)(body.Length - 6));
        body[4] = (byte)length; body[5] = (byte)(length >> 8);
        return body;
    }

    /// Returns:
    ///     byte[]: Native RPG progress, including a default-zero combat fraction on level-up.
    private static byte[] Progress(bool levelUp = false)
    {
        byte[] body = Wire.Body(writer =>
        {
            writer.Write(0u); writer.Write((ushort)0); writer.Write((byte)1);
            Mask(writer, 18, new[] { 6 }, Array.Empty<int>());
            Mask(writer, 16, levelUp ? new[] { 7 } : new[] { 6, 7, 8, 9, 10, 11 },
                levelUp ? new[] { 6 } : Array.Empty<int>());
            if (levelUp) writer.Write(2u);
            else
            {
                writer.Write(.4f); writer.Write(1u);
                writer.Write(.25f); writer.Write(2u);
                writer.Write(.3f); writer.Write(3u);
            }
        });
        ushort length = checked((ushort)(body.Length - 6));
        body[4] = (byte)length; body[5] = (byte)(length >> 8);
        return body;
    }

    /// <summary>Write native field-presence and schema-default bit planes.</summary>
    private static void Mask(BinaryWriter writer, int count, int[] present, int[] defaults)
    {
        bool extended = defaults.Length != 0;
        byte[] bytes = new byte[(4 + count * (extended ? 2 : 1) + 7) / 8];
        bytes[0] = extended ? (byte)6 : (byte)2;
        foreach (int field in present) bytes[(4 + field) / 8] |= (byte)(1 << ((4 + field) % 8));
        foreach (int field in defaults)
        {
            int bit = 4 + count + field;
            bytes[bit / 8] |= (byte)(1 << (bit % 8));
        }
        writer.Write(bytes);
    }
}
