using System;
using System.Diagnostics;
using System.IO;
using FortyTwoAndBeyond.Protocol;

internal static class TimedPercentChecks
{
    /// <summary>Check production-shaped resource curves, idle timing, partial revisions and static values.</summary>
    public static void Check()
    {
        const uint tick = 232666391;
        const uint end = 232690949;
        const float rate = .000035999997635f;
        const float start = .1158839911f;
        long now = Stopwatch.GetTimestamp();
        var curve = new TimedPercentState(true, end, rate, start, now);
        Near(curve.Value(tick, now), 1 - (end - tick) * rate, .00001f,
            "Production end-tick shield value");
        Near(curve.Value(tick + 10000, now), 1 - (end - tick - 10000) * rate, .00001f,
            "Advancing authoritative server tick");
        Near(curve.Value(null, now + Stopwatch.Frequency * 10), start + rate * 10000,
            .00001f, "Idle receipt-clock regeneration");
        Near(curve.Value(end + 1, now), 1, .00001f, "Regeneration endpoint");
        var noChange = curve.Merge(false, 0, false, 0, false, 0, null,
            now + Stopwatch.Frequency);
        Near(noChange.Value(null, now + Stopwatch.Frequency * 2), start + rate * 2000,
            .00001f, "Empty partial must not reanchor receipt time");
        var wrap = new TimedPercentState(true, 500, rate, .964f, now);
        Near(wrap.Value(uint.MaxValue - 499, now), 1 - 1000 * rate,
            .00001f, "Wrapped native end tick");
        var partial = curve.Merge(false, 0, false, 0, true, .25f, null,
            now + Stopwatch.Frequency);
        Near(partial.Value(null, now + Stopwatch.Frequency * 2), .25f + rate * 1000,
            .00001f, "Partial StartValue retaining rate");
        Near(partial.Value(tick + 2000, now + Stopwatch.Frequency), .25f,
            .00001f, "Partial StartValue supersedes stale end tick");
        var staticValue = partial.Merge(true, 0, true, 0, true, .9166386f, null, now);
        Near(staticValue.Value(null, now + Stopwatch.Frequency * 60), .9166386f,
            .00001f, "Static native resource after rate reset");
        var drain = new TimedPercentState(true, tick + 10000, -.00002f, .2f, now);
        Near(drain.Value(tick, now), .2f, .00001f, "Negative-rate native drain");
        Near(drain.Value(tick + 10001, now), 0, .00001f, "Drain endpoint");

        var world = new World();
        world.Apply(new Frame(0x1b, ShipResource(0x40000001, end, rate, start)), 0x40000001);
        if (!world.Equipment.ShieldPercent.Known) throw new Exception("Timed shield record was not retained.");
        var earlier = new Snapshot(world, "ready", true);
        world.Apply(new Frame(0x1b, ShipResource(0x40000001, null, null, .25f)), 0x40000001);
        if (world.Equipment.ShieldPercent.EndTick != end ||
            Math.Abs(world.Equipment.ShieldPercent.ChangePerMillisecond - rate) > 0.000000001f)
            throw new Exception("A partial native shield update discarded timing fields.");
        var later = new Snapshot(world, "ready", true);
        if (Math.Abs(earlier.Shield - start) > .001f ||
            Math.Abs(later.Shield - .25f) > .001f ||
            Math.Abs(earlier.Shield - later.Shield) < .1f)
            throw new Exception("Native resource snapshots were mutable or ignored a StartValue-only hit.");
        world.Apply(new Frame(0x1b, ShipResourceReset(0x40000001)), 0x40000001);
        var cleared = new Snapshot(world, "ready", true);
        if (cleared.Shield != 0 || world.Equipment.ShieldPercent.Value(null,
            Stopwatch.GetTimestamp()) != 0)
            throw new Exception("Native resource reset did not clear the timed curve.");
        Console.WriteLine("PASS: timed native shield/energy resource curves and partial AUX updates");
    }

    /// <summary>
    /// Returns:
    ///     byte[]: A bounded native own-ship shield AUX body with selected percent fields.
    /// </summary>
    private static byte[] ShipResource(uint owner, uint? end, float? rate, float? start)
    {
        byte[] body = Wire.Body(writer =>
        {
            writer.Write(owner); writer.Write((ushort)0); writer.Write((byte)1);
            Mask(writer, 58, 6);
            byte[] mask = new byte[1] { 2 };
            if (end.HasValue) mask[0] |= 1 << 4;
            if (rate.HasValue) mask[0] |= 1 << 5;
            if (start.HasValue) mask[0] |= 1 << 6;
            writer.Write(mask);
            if (end.HasValue) writer.Write(end.Value);
            if (rate.HasValue) writer.Write(rate.Value);
            if (start.HasValue) writer.Write(start.Value);
        });
        ushort length = checked((ushort)(body.Length - 6));
        body[4] = (byte)length; body[5] = (byte)(length >> 8);
        return body;
    }

    /// <summary>
    /// Returns:
    ///     byte[]: A native own-ship shield AUX reset body.
    /// </summary>
    private static byte[] ShipResourceReset(uint owner)
    {
        byte[] body = Wire.Body(writer =>
        {
            writer.Write(owner); writer.Write((ushort)0); writer.Write((byte)1);
            Mask(writer, 58, 6); writer.Write((byte)5);
        });
        ushort length = checked((ushort)(body.Length - 6));
        body[4] = (byte)length; body[5] = (byte)(length >> 8);
        return body;
    }

    /// <summary>Write a single native field mask with one selected field.</summary>
    private static void Mask(BinaryWriter writer, int count, int field)
    {
        byte[] flags = new byte[(count + 11) / 8]; flags[0] = 2;
        flags[(field + 4) / 8] |= (byte)(1 << ((field + 4) % 8));
        writer.Write(flags);
    }

    /// <summary>Fail on resource fractions outside a production-derived tolerance.</summary>
    private static void Near(float actual, float expected, float tolerance, string label)
    { if (Math.Abs(actual - expected) > tolerance) throw new Exception(label + ": " + actual + " versus " + expected); }
}
