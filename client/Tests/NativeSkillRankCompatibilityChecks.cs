using System;
using System.IO;
using System.Linq;
using FortyTwoAndBeyond.Protocol;

/// <summary>Exercise native partial skill ranks and explicit defaults without losing unrelated learned skills.</summary>
internal static class NativeSkillRankCompatibilityChecks
{
    /// <summary>Merge captured-shape rank/availability updates and preserve omitted ceilings and snapshots.</summary>
    public static void Check()
    {
        var world = new World();
        world.Apply(new Frame(0x1b, FullBook()), 91);
        var before = new Snapshot(world, "", true);
        world.AbilityCooldownEnds[55] = long.MaxValue;
        byte[] partial = PartialTraining(2, 2);
        Require(partial.Length == 48, "Synthetic native training update no longer matches the captured record shape.");
        world.Apply(new Frame(0x1b, partial), 91);
        SkillRank changed = world.Skills.Single(skill => skill.Id == 55);
        Require(changed.Rank == 2 && changed.Maximum == 3 && changed.MaximumKnown && changed.Availability == 4 &&
            changed.Gate == 1 && changed.RequiredLevel == 20 && changed.Cost == 2 && world.Skills.Length == 2 &&
            world.Skills.Single(skill => skill.Id == 1).Rank == 1,
            "Native rank-only update reset an omitted ceiling, lost availability data or dropped unrelated skills.");
        Require(world.Experience?.AvailableSkillPoints == 1 && world.AbilityCooldownEnds[55] == long.MaxValue &&
            AuxState.ReadSkillCooldowns(partial).Length == 0,
            "A rank/availability diff lost following skill points or changed an omitted cooldown.");
        Require(before.Skills.Single(skill => skill.Id == 55).Rank == 1 && before.Skills.Single(skill => skill.Id == 55).Maximum == 3,
            "Merged skill update mutated an earlier immutable snapshot.");
        var fresh = new World(); fresh.Apply(new Frame(0x1b, partial), 91);
        SkillRank incomplete = fresh.Skills.Single();
        Require(incomplete.Rank == 2 && !incomplete.MaximumKnown && !incomplete.CanTrain && incomplete.GateReason() == "WAITING FOR SKILL DATA",
            "A partial update before the first full skill snapshot crashed or enabled unverified training.");
        world.Apply(new Frame(0x1b, GlobalSkills(Wire.Body(writer =>
        { writer.Write(Mask(64, new[] { 55 })); writer.Write(Mask(7, new[] { 4 })); writer.Write(7u); }))), 91);
        Require(world.Skills.Single(skill => skill.Id == 55).Maximum == 3 && world.Skills.Single(skill => skill.Id == 55).QuestOnlyLevel == 7,
            "Quest-only rank limit overwrote the distinct maximum skill level.");
        byte[] slotReset = GlobalSkills(Wire.Body(writer =>
        { writer.Write(DefaultMask(64, 55)); writer.Write((byte)5); }));
        SkillRank[] reset = AuxState.ReadSkillRanks(slotReset, world.Skills);
        Require(reset.Length == 1 && reset[0].Id == 1, "Explicit slot reset did not remove only its previous skill.");
        Require(AuxState.ReadSkillCooldowns(slotReset).Single().DurationMs == 0,
            "Explicit skill slot reset retained its old cooldown.");
        Require(AuxState.ReadSkillRanks(GlobalSkills(new byte[] { 5 }), world.Skills).Length == 0,
            "Whole skill collection reset retained earlier learned ranks.");
        byte[] fieldReset = GlobalSkills(Wire.Body(writer =>
        { writer.Write(Mask(64, new[] { 55 })); writer.Write(DefaultMask(7, 0, 3)); }));
        SkillRank defaults = AuxState.ReadSkillRanks(fieldReset, world.Skills).Single(skill => skill.Id == 55);
        Require(defaults.Rank == 0 && defaults.Maximum == 3 && defaults.Availability == 1 && defaults.Gate == 0 && defaults.Cost == 0,
            "Nested field defaults were treated as omitted unchanged fields.");
        byte[] cooldownReset = GlobalSkills(Wire.Body(writer =>
        { writer.Write(Mask(64, new[] { 55 })); writer.Write(DefaultMask(7, 1)); }));
        SkillCooldown cleared = AuxState.ReadSkillCooldowns(cooldownReset).Single();
        Require(cleared.SkillId == 55 && cleared.DurationMs == 0,
            "Explicit recycle-time default did not clear that skill's cooldown.");
        Reject(() => AuxState.ReadSkillRanks(PartialTraining(4, 2), world.Skills));
        Reject(() => AuxState.ReadSkillRanks(PartialTraining(2, 10), world.Skills));
        Reject(() => AuxState.ReadSkillRanks(partial.Take(partial.Length - 5).ToArray(), world.Skills));
    }

    /// Returns:
    ///     byte[]: A two-skill initial snapshot with different authoritative maximum ranks.
    private static byte[] FullBook() => GlobalSkills(Wire.Body(writer =>
    {
        writer.Write(Mask(64, new[] { 1, 55 }));
        foreach (uint id in new uint[] { 1, 55 })
        {
            writer.Write(Mask(7, new[] { 0, 3, 4, 5 })); writer.Write(1u);
            writer.Write(4u); writer.Write(0u); writer.Write(0u); writer.Write(1u);
            writer.Write(9u); writer.Write(id == 55 ? 3u : 9u);
        }
    }));

    /// Returns:
    ///     byte[]: A synthetic version of the production training diff: rank and availability only, followed by skill points.
    private static byte[] PartialTraining(uint rank, uint cost) => GlobalSkills(Wire.Body(writer =>
    {
        writer.Write(Mask(64, new[] { 55 })); writer.Write(Mask(7, new[] { 0, 3 }));
        writer.Write(rank); writer.Write(4u); writer.Write(1u); writer.Write(20u); writer.Write(cost);
    }), points: true);

    /// Returns:
    ///     byte[]: One complete native global/RPG envelope carrying the requested skill collection.
    private static byte[] GlobalSkills(byte[] slots, bool points = false)
    {
        byte[] body = Wire.Body(writer =>
        {
            writer.Write(0u); writer.Write((ushort)0); writer.Write((byte)1);
            writer.Write(Mask(18, new[] { 6 })); writer.Write(Mask(16, points ? new[] { 2, 4 } : new[] { 2 }));
            writer.Write(slots); if (points) writer.Write(1u);
        });
        ushort size = (ushort)(body.Length - 6); body[4] = (byte)size; body[5] = (byte)(size >> 8); return body;
    }

    /// Returns:
    ///     byte[]: A one-bit native field mask.
    private static byte[] Mask(int count, int[] present)
    {
        byte[] bytes = new byte[(4 + count + 7) / 8]; bytes[0] = 2;
        foreach (int field in present) bytes[(4 + field) / 8] |= (byte)(1 << ((4 + field) % 8));
        return bytes;
    }

    /// Returns:
    ///     byte[]: A two-bit native mask with explicit reset bits and no populated values.
    private static byte[] DefaultMask(int count, params int[] defaults)
    {
        byte[] bytes = new byte[(4 + count * 2 + 7) / 8]; bytes[0] = 6;
        foreach (int field in defaults) bytes[(4 + count + field) / 8] |= (byte)(1 << ((4 + count + field) % 8));
        return bytes;
    }

    /// <summary>Require invalid native values and truncated revisions to remain protocol failures.</summary>
    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Invalid native skill update was accepted."); }

    /// <summary>Fail a concrete partial-update or reset contract.</summary>
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
