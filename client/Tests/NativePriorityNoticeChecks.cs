using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using FortyTwoAndBeyond.Protocol;

/// <summary>Verify production-native equipment rejections, XP awards and credits reach visible notices once.</summary>
internal static class NativePriorityNoticeChecks
{
    /// <summary>Decode captured-shape priority/push notices without deriving authoritative resource values from text.</summary>
    public static void Check()
    {
        const string denied = "You need more skill to equip this item";
        const string xp = "Defeated Rabid Hound Bot: 1400 Combat experience earned.";
        const string credits = "You have gained 62 credits";
        byte[] failure = Notice(denied, "MessageLine", 2000, 4);
        Require(failure.Length == 59 && Notice(xp, "MessageLine", 4000, 4).Length == 77 &&
            Notice(credits, "MessageLine", 5000, 4).Length == 47, "Native priority fixtures diverged from the captured wire shapes.");
        NativePriorityNotice decoded = NativePriorityNotice.Decode(failure);
        Require(decoded.Message == denied && decoded.Destination == "MessageLine" && decoded.DurationMs == 2000 && decoded.Priority == 4,
            "Equipment rejection text or native presentation metadata was lost.");
        var world = new World();
        world.Apply(new Frame(0x1b, Experience()), 91);
        ExperienceState experience = world.Experience;
        world.Apply(new Frame(0x20, failure), 91);
        var before = new Snapshot(world, "", true);
        world.Apply(new Frame(0x20, Notice(xp, "MessageLine", 4000, 4)), 91);
        world.Apply(new Frame(0x22, Notice(credits, "MessageLine", 5000, 3)), 91);
        var after = new Snapshot(world, "", true);
        Require(after.Messages.SequenceEqual(new[] { denied, xp, credits }) && before.Messages.SequenceEqual(new[] { denied }),
            "Native notices were duplicated, hidden or mutated an earlier history snapshot.");
        Require(ReferenceEquals(world.Experience, experience) && after.Experience.CombatLevel == 2 &&
            after.Experience.CombatFraction == .25f && after.Credits == 0,
            "A notice invented XP, levels or credit changes instead of waiting for authoritative AUX data.");
        using (var session = new LocalSession(CancellationToken.None))
        {
            typeof(LocalSession).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(session, new object[] { new Frame(0x20, failure) });
            Require(session.Status == denied && session.World.Messages.Count == 1,
                "Native rejection did not reach both current status and the visible communication history exactly once.");
        }
        for (int index = 0; index < 205; index++) world.Apply(new Frame(0x20, failure), 91);
        Require(world.Messages.Count == 200 && before.Messages.Length == 1, "Native priority notices bypassed bounded immutable scrollback.");
        Require(NativePriorityNotice.Decode(Notice("Énergie restaurée", "MessageLine", 2000, 4)).Message == "Énergie restaurée",
            "Valid Unicode server notices were corrupted.");
        Reject(failure.Take(failure.Length - 1).ToArray());
        Reject(failure.Concat(new byte[] { 0 }).ToArray());
        Reject(Notice("", "MessageLine", 2000, 4));
        Reject(Notice(denied, "", 2000, 4));
        Reject(Notice(denied, "MessageLine", uint.MaxValue, 4));
        Reject(Notice(denied, "MessageLine", 2000, uint.MaxValue));
        Reject(Notice("Bad\u001bnotice", "MessageLine", 2000, 4));
        Reject(Notice(denied, "Bad\nTarget", 2000, 4));
        Reject(Notice(new string('a', 2049), "MessageLine", 2000, 4));
        byte[] badUtf8 = (byte[])failure.Clone(); badUtf8[0] = 0xff; Reject(badUtf8);
    }

    /// Returns:
    ///     byte[]: A synthetic native pair of NUL-terminated strings and little-endian metadata.
    private static byte[] Notice(string message, string destination, uint duration, uint priority) => Wire.Body(writer =>
    { writer.Write(Encoding.UTF8.GetBytes(message)); writer.Write((byte)0);
        writer.Write(Encoding.UTF8.GetBytes(destination)); writer.Write((byte)0);
        writer.Write(duration); writer.Write(priority); });

    /// Returns:
    ///     byte[]: An independent authoritative RPG progress update, not a textual award.
    private static byte[] Experience()
    {
        byte[] body = Wire.Body(writer =>
        {
            writer.Write(0u); writer.Write((ushort)0); writer.Write((byte)1);
            writer.Write(Mask(18, new[] { 6 })); writer.Write(Mask(16, new[] { 6, 7 }));
            writer.Write(.25f); writer.Write(2u);
        });
        ushort length = (ushort)(body.Length - 6); body[4] = (byte)length; body[5] = (byte)(length >> 8); return body;
    }

    /// Returns:
    ///     byte[]: A one-bit native field-presence mask.
    private static byte[] Mask(int count, int[] present)
    {
        byte[] mask = new byte[(4 + count + 7) / 8]; mask[0] = 2;
        foreach (int field in present) mask[(4 + field) / 8] |= (byte)(1 << ((4 + field) % 8));
        return mask;
    }

    /// <summary>Require malformed notice envelopes to remain explicit protocol failures.</summary>
    private static void Reject(byte[] body)
    { try { NativePriorityNotice.Decode(body); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Malformed native priority notice was accepted."); }

    /// <summary>Fail the concrete native notice display or authority contract.</summary>
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
