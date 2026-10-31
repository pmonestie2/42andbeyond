using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FortyTwoAndBeyond.Client;
using FortyTwoAndBeyond.Protocol;

/// <summary>Verify production-native corpse creation, pose, AUX contents and selection without custom map packets.</summary>
internal static class NativeWreckCompatibilityChecks
{
    private const uint Player = 81, Wreck = 120;

    /// <summary>Exercise the native wreck path and preserve earlier immutable display snapshots.</summary>
    public static void Check()
    {
        var world = new World();
        world.Apply(new Frame(0x3e, Wire.Body(writer =>
        { writer.Write((ushort)0); writer.Write(Player); writer.Write(1u);
            writer.Write(0f); writer.Write(0f); writer.Write(0f);
            writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f); })), Player);
        world.Apply(new Frame(4, Create(25)), Player);
        Require(world.Objects.TryGetValue(Wreck, out MapObject created) && created.Kind == 25 && !world.Mobs.ContainsKey(Wreck),
            "Native hulk creation was discarded or mistaken for a live ship.");
        world.Apply(new Frame(0x40, Pose(100, 200, 300)), Player);
        world.Apply(new Frame(0x1b, CorpseAux(false)), Player);
        var beforeLoot = new Snapshot(world, "", true);
        Require(beforeLoot.Objects.Single().Name == "Synthetic wreck" && beforeLoot.Objects.Single().Position.X == 100 &&
            beforeLoot.Objects.Single().Position.Y == 200 && beforeLoot.Objects.Single().Position.Z == 300,
            "Native corpse pose or name was lost before its inventory arrived.");
        byte[] nativeContents = CorpseAux(true);
        ushort undercountedLength = (ushort)(nativeContents.Length - 8);
        nativeContents[4] = (byte)undercountedLength; nativeContents[5] = (byte)(undercountedLength >> 8);
        world.Apply(new Frame(0x1b, nativeContents), Player);
        world.SetOpenLoot(Wreck);
        var snapshot = new Snapshot(world, "", true);
        Require(snapshot.Loot.Single().Items.Count(item => item != null) == 2 && snapshot.Loot.Single().Items[0].Template == 301 &&
            snapshot.Loot.Single().Items[1].Quantity == 3 && snapshot.OpenLootId == Wreck,
            "Native corpse AUX did not expose actual loot slots in the opened wreck snapshot.");
        Require(ThreatSelection.NearestSelectable(snapshot) == Wreck && !snapshot.Objects.Single().Navigable,
            "A native wreck could not be selected as a corpse or was treated as navigation.");
        world.Apply(new Frame(0x40, Pose(900, 800, 700)), Player);
        Require(beforeLoot.Objects.Single().Position.X == 100 && beforeLoot.Loot.Length == 0 &&
            snapshot.Objects.Single().Position.X == 100 && snapshot.Loot.Single().Items[1].Quantity == 3,
            "Later corpse updates mutated earlier display snapshots.");
        world.Apply(new Frame(7, Wire.Body(writer => writer.Write(Wreck))), Player);
        Require(world.Objects.Count == 0 && world.Loot.Count == 0 && world.OpenLootId == 0 && snapshot.Loot.Length == 1,
            "Native corpse removal left live contents or changed an earlier snapshot.");
        var unknown = new World(); unknown.Apply(new Frame(4, Create(26)), Player);
        Require(unknown.Objects.Count == 0 && unknown.Mobs.Count == 0, "Unimplemented native object types were guessed to be wrecks.");
        Reject(() => MapObject.FromNativeWreck(Create(0)));
        Reject(() => MapObject.FromNativeWreck(Create(25).Take(22).ToArray()));
        Reject(() => created.WithConstantPose(Pose(1, 2, 3).Take(31).ToArray()));
        byte[] badEnvelope = CorpseAux(true); badEnvelope[4]--;
        var strictWorld = new World(); strictWorld.Apply(new Frame(4, Create(25)), Player);
        Reject(() => strictWorld.Apply(new Frame(0x1b, badEnvelope), Player));
    }

    /// Returns:
    ///     int: Actual nonempty native corpse inventories decoded from complete records of a private capture.
    public static int CheckCapturedWrecks(string path)
    {
        var worlds = new Dictionary<uint, World>();
        var decoded = new HashSet<ulong>();
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new BinaryReader(stream))
        {
            long prefix = stream.Length;
            while (prefix - stream.Position >= 10)
            {
                uint owner = reader.ReadUInt32(); ushort opcode = reader.ReadUInt16(); uint length = reader.ReadUInt32();
                if (length > 32763) throw new InvalidDataException("Captured native frame exceeds its bound.");
                if (prefix - stream.Position < length) break;
                byte[] body = reader.ReadBytes((int)length);
                if (!worlds.TryGetValue(owner, out World world)) worlds[owner] = world = new World();
                bool creates = opcode == 4 && body.Length == 23 && body[10] == 25;
                bool known = body.Length >= 4 && world.Objects.ContainsKey(Wire.U32(body, 0));
                if (!creates && !(known && (opcode == 0x40 || opcode == 0x1b || opcode == 7))) continue;
                world.Apply(new Frame(opcode, body), owner);
                if (opcode == 0x1b && world.Loot.TryGetValue(Wire.U32(body, 0), out ItemState[] slots) &&
                    slots.Any(item => item?.Present == true && item.Quantity > 0))
                {
                    var snapshot = new Snapshot(world, "", true);
                    Require(snapshot.Loot.Any(corpse => corpse.Id == Wire.U32(body, 0) && corpse.Items.Any(item => item != null)),
                        "Captured native corpse contents failed to reach the immutable client snapshot.");
                    decoded.Add(((ulong)owner << 32) | Wire.U32(body, 0));
                }
            }
        }
        Require(decoded.Count > 0, "Private capture did not decode a nonempty native corpse inventory.");
        return decoded.Count;
    }

    /// Returns:
    ///     byte[]: A native 23-byte CreateObject envelope for the isolated wreck identity.
    internal static byte[] Create(byte kind) => Wire.Body(writer =>
    { writer.Write(Wreck); writer.Write(21f); writer.Write((ushort)1960); writer.Write(kind); writer.Write(new byte[12]); });

    /// Returns:
    ///     byte[]: A native constant position and quaternion update.
    internal static byte[] Pose(float x, float y, float z) => Wire.Body(writer =>
    { writer.Write(Wreck); writer.Write(x); writer.Write(y); writer.Write(z); writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f); });

    /// Returns:
    ///     byte[]: A synthetic name-only or full native HulkIndex AUX record.
    internal static byte[] CorpseAux(bool contents)
    {
        byte[] body = Wire.Body(writer =>
        {
            writer.Write(Wreck); writer.Write((ushort)0); writer.Write((byte)1);
            writer.Write(contents ? new byte[] { 0x16, 0xec, 0x0f } : new byte[] { 0x16, 0xe0, 0x03 });
            Text(writer, "Synthetic wreck");
            writer.Write(new byte[] { 5, 5, 5, 5 });
            if (!contents) return;
            writer.Write((byte)5);
            writer.Write(PresentMask(40, new[] { 0, 1 }));
            foreach (uint template in new uint[] { 301, 302 })
            {
                writer.Write(PresentMask(10, new[] { 0, 1, 4, 5 }));
                writer.Write(template); writer.Write(template == 301 ? 1u : 3u); writer.Write(1f); writer.Write(1f);
            }
        });
        byte[] size = BitConverter.GetBytes((ushort)(body.Length - 6)); body[4] = size[0]; body[5] = size[1];
        return body;
    }

    /// Returns:
    ///     byte[]: A native one-bit presence mask for a fixed-width AUX container.
    internal static byte[] PresentMask(int count, int[] present)
    {
        byte[] mask = new byte[(4 + count + 7) / 8]; mask[0] = 2;
        foreach (int field in present) mask[(4 + field) / 8] |= (byte)(1 << ((4 + field) % 8));
        return mask;
    }

    /// <summary>Write one synthetic native length-prefixed name.</summary>
    private static void Text(BinaryWriter writer, string text)
    { byte[] bytes = Encoding.ASCII.GetBytes(text); writer.Write((ushort)bytes.Length); writer.Write(bytes); }

    /// <summary>Ensure malformed object identities and poses remain protocol errors.</summary>
    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Malformed native wreck data was accepted."); }

    /// <summary>Fail with the concrete native wreck contract that regressed.</summary>
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
