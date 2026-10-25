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

    /// <summary>Verify native ally and self shield and hull repairs on a disposable server.</summary>
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
            if (!caster.World.AvailableAbilities.Contains(77u) ||
                !caster.World.AvailableAbilities.Contains(95u))
                throw new Exception("Hull Patch or Repair Hull was not advertised for TT.");
            caster.ActivateAbilityFor(95, recipient.Player);
            Wait(caster, recipient, () => caster.World.Equipment.TargetId == recipient.Player &&
                caster.World.Messages.Any(message => message.Contains("Hull repair completed.")),
                "Acknowledged ally Repair Hull did not complete.");
            caster.ActivateAbility(77);
            Wait(caster, recipient, () => caster.World.Messages.Count(message =>
                message.Contains("Hull repair completed.")) >= 2,
                "Self Patch Hull did not complete with an ally selected.");
            if (!caster.World.AvailableAbilities.Contains(31u) ||
                !caster.World.AvailableAbilities.Contains(32u) ||
                !caster.World.AvailableAbilities.Contains(91u))
                throw new Exception("Single-target rank 1/5 support abilities were not advertised for TT.");
            caster.ActivateAbilityFor(31, recipient.Player);
            Wait(caster, recipient, () => caster.World.Equipment.TargetId == recipient.Player &&
                caster.World.Messages.Count(message => message.Contains("Hull repair completed.")) >= 3,
                "Combat Hull Repair did not complete for the selected ally.");
            float energyBeforeShields = caster.World.Equipment.Energy;
            caster.ActivateAbilityFor(32, recipient.Player);
            Wait(caster, recipient, () => caster.World.Equipment.Energy < energyBeforeShields &&
                caster.Status == "Recharge Shields completed.",
                "Combat Shield Recharge did not complete for the selected ally.");
            float energyBeforeSelf = caster.World.Equipment.Energy;
            caster.ActivateAbility(91);
            Wait(caster, recipient, () => caster.World.Equipment.Energy < energyBeforeSelf &&
                caster.Status == "Recharge Shields completed.",
                "Self Regenerate Shields did not complete with an ally selected.");
            Console.WriteLine("PASS: native selected-ally and self shield/hull support casts");
        }
    }
}
