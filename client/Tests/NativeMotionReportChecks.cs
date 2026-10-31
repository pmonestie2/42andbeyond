using System;
using System.IO;
using FortyTwoAndBeyond.Protocol;

internal static class NativeMotionReportChecks
{
    /// <summary>Check captured MVAS layout and continuous speed through sparse own-pose feedback.</summary>
    public static void Check()
    {
        byte[] body = NativeMotionReport.Encode(new Position(-56231.8086f, 87309.9375f, -11.48f),
            new Position(-.982917f, -.120567f, -.139051f), 2230);
        if (body.Length != 28 || Wire.U32(body, 24) != 2230 ||
            Math.Abs(Wire.Float(body, 0) + 56231.8086f) > .001f)
            throw new Exception("Native MVAS position/speed body differs from Windows capture.");
        if (Wire.U32(NativeMotionReport.Encode(default, new Position(1, 0, 0), 4700), 24) != 4700 ||
            unchecked((int)Wire.U32(NativeMotionReport.Encode(default, new Position(1, 0, 0), -250), 24)) != -250)
            throw new Exception("Native MVAS signed speed trailer changed.");
        float hx = Wire.Float(body, 12), hy = Wire.Float(body, 16), hz = Wire.Float(body, 20);
        if (Math.Abs(hx * hx + hy * hy + hz * hz - 1) > .0001f)
            throw new Exception("Native MVAS heading was not normalized.");
        float speed = 0, distance = 0;
        for (int step = 0; step < 50; step++)
        {
            float prior = speed;
            speed = NativeMotionReport.AdvanceSpeed(speed, 100, 20, .1f);
            distance += (prior + speed) * .5f * .1f;
        }
        if (Math.Abs(speed - 100) > .001f || Math.Abs(distance - 250) > .1f)
            throw new Exception("Free flight stopped accelerating while own poses were sparse.");
        for (int step = 0; step < 50; step++)
            speed = NativeMotionReport.AdvanceSpeed(speed, 0, 20, .1f);
        if (speed != 0) throw new Exception("Native motion prediction kept speed after braking.");
        speed = 100; distance = 0;
        for (int step = 0; step < 600; step++)
        {
            speed = NativeMotionReport.AdvanceSpeed(speed, 100, 20, .1f);
            distance += speed * .1f;
            if (Wire.U32(NativeMotionReport.Encode(default, new Position(1, 0, 0), speed), 24) != 100)
                throw new Exception("Cruise report encoded elapsed time as speed.");
        }
        if (speed != 100 || Math.Abs(distance - 6000) > .1f)
            throw new Exception("Sixty-second cruise diverged from the server target speed.");
        byte[] limits = Wire.Body(writer =>
        {
            writer.Write(1u); writer.Write((ushort)23); writer.Write((byte)1);
            writer.Write(new byte[] { 2, 0, 0x3e, 0, 0, 0, 0, 0 });
            writer.Write(174.3f); writer.Write(-87.15f); writer.Write(140f);
            writer.Write((byte)0); writer.Write((byte)0);
        });
        var world = new World(); world.Apply(new Frame(0x1b, limits), 1);
        Snapshot ship = new Snapshot(world, "ready", true);
        if (Math.Abs(ship.MaxImpulseSpeed.GetValueOrDefault() - 174.3f) > .001f ||
            Math.Abs(ship.MinImpulseSpeed.GetValueOrDefault() + 87.15f) > .001f ||
            Math.Abs(ship.ImpulseAcceleration.GetValueOrDefault() - 140f) > .001f ||
            ship.SpeedLocked || ship.OrientationLocked || new Snapshot(new World(), "new sector", false).MaxImpulseSpeed.HasValue)
            throw new Exception("Native ship impulse limits were not retained across an immutable snapshot.");
        byte[] revisedLimit = Wire.Body(writer =>
        {
            writer.Write(1u); writer.Write((ushort)13); writer.Write((byte)1);
            writer.Write(new byte[] { 2, 0, 2, 0, 0, 0, 0, 0 }); writer.Write(200f);
        });
        world.Apply(new Frame(0x1b, revisedLimit), 1);
        Snapshot revised = new Snapshot(world, "ready", true);
        if (ship.MaxImpulseSpeed != 174.3f || revised.MaxImpulseSpeed != 200f ||
            revised.MinImpulseSpeed != ship.MinImpulseSpeed ||
            revised.ImpulseAcceleration != ship.ImpulseAcceleration)
            throw new Exception("A partial impulse update changed prior snapshots or omitted limits.");
        byte[] angularOnly = Wire.Body(writer =>
        {
            writer.Write((ushort)0x28); writer.Write(1u); writer.Write(1000u);
            writer.Write(0f); writer.Write(0f); writer.Write(0f);
            writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f);
            writer.Write(0u); writer.Write(0f); writer.Write(0f);
        });
        world.Apply(new Frame(0x3e, angularOnly), 1);
        if (world.OwnMotionSequence != 0)
            throw new Exception("A pose-only packet incorrectly reset predicted impulse speed.");
        byte[] motion = Wire.Body(writer =>
        {
            writer.Write((ushort)7); writer.Write(1u); writer.Write(1500u);
            writer.Write(0f); writer.Write(0f); writer.Write(0f);
            writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f);
            writer.Write(1u); writer.Write(.02f); writer.Write(.1743f); writer.Write(.14f);
        });
        world.Apply(new Frame(0x3e, motion), 1);
        if (world.OwnMotionSequence != 1 || Math.Abs(world.Speed - 20) > .01f ||
            Math.Abs(world.TargetSpeed - 174.3f) > .01f || Math.Abs(world.Acceleration - 140) > .01f)
            throw new Exception("Authoritative impulse telemetry did not replace the prior speed.");
        var throttle = new FortyTwoAndBeyond.Client.FlightThrottle();
        byte? forward = throttle.Sample(true, true, false, false);
        float target = NativeMotionReport.ControlTarget(forward.GetValueOrDefault(), 0, 174.3f, -87.15f);
        speed = NativeMotionReport.AdvanceSpeed(0, target, 140, .1f);
        byte? brake = throttle.Sample(true, false, true, false, speed, target);
        if (forward != 2 || brake != 4 || speed < 13.9f)
            throw new Exception("S did not brake a predicted W impulse before the next server pose.");
        speed = NativeMotionReport.AdvanceSpeed(speed,
            NativeMotionReport.ControlTarget(brake.Value, target, 174.3f, -87.15f), 140, .1f);
        throttle.Sample(true, false, false, false);
        byte? reverse = throttle.Sample(true, false, true, false, speed, 0);
        if (speed != 0 || reverse != 3 ||
            NativeMotionReport.ControlTarget(reverse.Value, 0, 174.3f, -87.15f) != -87.15f)
            throw new Exception("Fresh S press did not reverse after braking to rest.");
        speed = 0;
        for (int step = 0; step < 600; step++)
        {
            speed = NativeMotionReport.AdvanceSpeed(speed, 174.3f, 140, .1f);
            if (speed > 174.3f || Wire.U32(NativeMotionReport.Encode(default,
                new Position(1, 0, 0), speed), 24) > 175)
                throw new Exception("A minute of flight exceeded authoritative impulse speed.");
        }
        try { NativeMotionReport.Encode(default, default, 0); }
        catch (InvalidDataException) { Console.WriteLine("PASS: native MVAS position, heading and sparse-pose flight prediction"); return; }
        throw new Exception("Native motion report accepted an empty heading.");
    }
}
