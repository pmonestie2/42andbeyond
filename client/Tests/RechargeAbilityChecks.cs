using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using FortyTwoAndBeyond.Protocol;

internal static class RechargeAbilityChecks
{
    /// <summary>Poll both disposable pilots until the specified native state arrives.</summary>
    private static void Wait(LocalSession caster, LocalSession recipient, Func<bool> ready,
        string failure)
    {
        var deadline = Stopwatch.StartNew();
        while (!ready())
        {
            if (deadline.Elapsed.TotalSeconds > 12) throw new TimeoutException(failure);
            caster.Poll(); recipient.Poll();
        }
    }

    /// <summary>Verify acknowledged ally and self Recharge Shields casts on a disposable server.</summary>
    public static void Live(string host)
    {
        using (var caster = new LocalSession(CancellationToken.None))
        using (var recipient = new LocalSession(CancellationToken.None))
        {
            caster.Connect(host, "ptt", "ptt");
            recipient.Connect(host, "pje", "pje");
            Wait(caster, recipient, () => caster.Ready && recipient.Ready &&
                caster.World.AvailableAbilities.Contains(89u) &&
                caster.World.Players.TryGetValue(recipient.Player, out ShipActor peer) &&
                peer.HasPosition, "Recharge Shields or recipient was not advertised.");
            caster.ActivateAbilityFor(89, recipient.Player);
            Wait(caster, recipient, () => caster.World.Equipment.TargetId == recipient.Player &&
                caster.World.Messages.Any(message => message.Contains("Recharge Shields completed.")),
                "Acknowledged ally Recharge Shields did not complete.");
            if (caster.World.Messages.Any(message => message.Contains("out of range") ||
                    message.Contains("Select a visible pilot")))
                throw new Exception("Recharge Shields selected an invalid ally.");
            caster.ActivateAbilityFor(89, caster.Player);
            Wait(caster, recipient, () => caster.World.Equipment.TargetId == caster.Player &&
                caster.World.Messages.Count(message => message.Contains("Recharge Shields completed.")) >= 2,
                "Acknowledged self Recharge Shields did not complete.");
            Console.WriteLine("PASS: native Recharge Shields target acknowledgment, ally cast, self cast");
        }
    }
}
