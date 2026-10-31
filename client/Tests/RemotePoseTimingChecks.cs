using System;
using FortyTwoAndBeyond.Client;

internal static class RemotePoseTimingChecks
{
    /// <summary>Check the production MOB update cadence and bounded visual prediction.</summary>
    public static void Check()
    {
        float duration = RemotePoseTiming.Duration(.515f);
        if (Math.Abs(duration - .515f) > .001f ||
            Math.Abs(RemotePoseTiming.Fraction(.2575f, duration) - .5f) > .001f ||
            RemotePoseTiming.Fraction(.8f, duration) != 1)
            throw new Exception("A 515 ms production MOB pose did not blend across its interval.");
        if (RemotePoseTiming.Duration(.02f) != .25f ||
            RemotePoseTiming.Duration(5.063f) != .8f)
            throw new Exception("Burst and delayed MOB samples exceeded visual timing bounds.");
        if (RemotePoseTiming.Teleport(4.56f, 5.063f) ||
            RemotePoseTiming.Teleport(25f, .5f) ||
            !RemotePoseTiming.Teleport(80f, .5f))
            throw new Exception("Cadence-scaled MOB teleport detection disagrees with captured movement.");
        Console.WriteLine("PASS: production MOB cadence interpolation and adaptive teleport guard");
    }
}
