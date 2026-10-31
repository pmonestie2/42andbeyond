using System;
using System.Reflection;
using FortyTwoAndBeyond.Protocol;

internal static class SectorLivenessChecks
{
    /// <summary>Distinguish quiet native sectors from failed startup and stalled reliable gaps.</summary>
    public static void Check()
    {
        MethodInfo decision = typeof(LocalSession).GetMethod("SectorTimedOut",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (decision == null) throw new MissingMethodException("Sector liveness decision is missing.");
        bool Expired(bool ready, double now, double entered, uint missing,
            double gap, double progress) =>
            (bool)decision.Invoke(null, new object[] { ready, now, entered, missing, gap, progress });
        if (Expired(true, 120, 1, 0, 0, 1))
            throw new Exception("An authenticated but idle native sector was disconnected.");
        if (Expired(false, 29, 1, 0, 0, 1) || !Expired(false, 32, 1, 0, 0, 1))
            throw new Exception("Initial sector handoff deadline changed.");
        if (Expired(true, 94, 1, 3, 50, 50) || !Expired(true, 96, 1, 3, 50, 50))
            throw new Exception("Reliable missing-range stall deadline changed.");
        if (Expired(true, 100, 1, 3, 50, 90) || Expired(true, 100, 1, 0, 50, 50))
            throw new Exception("Stream progress or gap closure failed to clear the stall.");
        Console.WriteLine("PASS: idle sector, startup and stalled reliable range liveness");
    }
}
