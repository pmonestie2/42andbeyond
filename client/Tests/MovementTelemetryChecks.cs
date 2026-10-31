using System;
using System.IO;
using FortyTwoAndBeyond.Protocol;

internal static class MovementTelemetryChecks
{
    /// <summary>Check captured compact masks preserve omitted speed, target and angular state.</summary>
    public static void Check()
    {
        byte[] Packet(ushort flags, params float[] values) => Wire.Body(writer =>
        {
            writer.Write(flags); writer.Write(1u); writer.Write(123u);
            writer.Write(new byte[12]); writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f);
            writer.Write(1u); foreach (float value in values) writer.Write(value);
        });
        var world = new World();
        world.Apply(new Frame(0x3e, Packet(0x7f, .1f, .2f, .03f, .01f, 0, .02f, 0)), 1);
        world.Apply(new Frame(0x3e, Packet(0x28, -.04f, -.05f)), 1);
        if (Math.Abs(world.Speed - 100) > .01 || Math.Abs(world.TargetSpeed - 200) > .01 ||
            Math.Abs(world.Acceleration - 30) > .01 || world.AngularY != -.04f || world.AngularZ != -.05f)
            throw new Exception("Production rotation-only update corrupted retained speed or acceleration.");
        world.Apply(new Frame(0x3e, Packet(0x79, .05421f, 0, 0, -.00002f, 0)), 1);
        if (Math.Abs(world.Speed - 54.21f) > .01 || world.TargetSpeed != world.Speed || world.AngularZ != -.00002f)
            throw new Exception("Production current-speed and rotation revision was misaligned.");
        world.Apply(new Frame(0x3e, Packet(4, -.14f)), 1);
        if (Math.Abs(world.Speed - 54.21f) > .01 || world.Acceleration != 140)
            throw new Exception("Acceleration-only update was mistaken for ship speed.");
        byte[] PacketAt(ushort flags, uint tick, float x, params float[] values) => Wire.Body(writer =>
        {
            writer.Write(flags); writer.Write(1u); writer.Write(tick);
            writer.Write(x); writer.Write(0f); writer.Write(0f);
            writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f);
            writer.Write(1u); foreach (float value in values) writer.Write(value);
        });
        var stop = new World();
        stop.Apply(new Frame(0x3e, PacketAt(0x79, 1000, 0, .05f, 0, 0, 0, 0)), 1);
        stop.Apply(new Frame(0x3e, PacketAt(0x28, 1094, 5, 0, 0)), 1);
        if (stop.Speed != 50) throw new Exception("A moving angular-only interleave erased speed.");
        stop.Apply(new Frame(0x3e, PacketAt(0x28, 1594, 5, 0, 0)), 1);
        if (stop.Speed != 0 || stop.TargetSpeed != 0 || stop.Acceleration != 0)
            throw new Exception("Repeated authoritative stationary pose retained stale forward motion.");
        stop.Apply(new Frame(0x3e, PacketAt(0x79, 2000, 10, .05f, 0, 0, 0, 0)), 1);
        stop.Apply(new Frame(0x3e, PacketAt(0x28, 2000, 10, 0, 0)), 1);
        if (stop.Speed != 50) throw new Exception("Duplicate tick falsely inferred a stop.");
        byte[] Phase(uint local, uint global) => Wire.Body(writer =>
        {
            writer.Write(1u); writer.Write((ushort)17); writer.Write((byte)1);
            writer.Write(new byte[] { 2, 0, 0, 0, 0x18, 0, 0, 0 });
            writer.Write(local); writer.Write(global);
        });
        stop.Apply(new Frame(0x1b, Phase(2, 2)), 1);
        stop.Apply(new Frame(0x3e, PacketAt(0x79, 2400, 810, 1.900475f, 0, 0, 0, 0)), 1);
        stop.Apply(new Frame(0x1b, Phase(8, 3)), 1);
        if (stop.Speed != 0 || stop.TargetSpeed != 0 || stop.Acceleration != 0)
            throw new Exception("Warp arrival retained cruise speed as free-flight motion.");
        stop.Apply(new Frame(0x3e, PacketAt(0x79, 2500, 1000, 1.900475f, 0, 0, 0, 0)), 1);
        stop.Apply(new Frame(0x1b, Phase(0, 0)), 1);
        if (stop.Speed != 0 || stop.TargetSpeed != 0)
            throw new Exception("Late warp positional frame survived the idle transition.");
        byte[] bad = Packet(0x28, .1f);
        try { MovementTelemetry.Decode(bad); }
        catch (InvalidDataException) { Console.WriteLine("PASS: compact production movement masks and retained telemetry"); return; }
        throw new Exception("Truncated compact movement record was accepted.");
    }
}
