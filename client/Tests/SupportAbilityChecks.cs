using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using FortyTwoAndBeyond.Protocol;

internal static class SupportAbilityChecks
{
    /// <summary>Poll two disposable pilots until their server state meets one condition.</summary>
    private static void Wait(LocalSession source, LocalSession target, Func<bool> ready,
        string failure)
    {
        var deadline = Stopwatch.StartNew();
        while (!ready())
        {
            if (deadline.Elapsed.TotalSeconds > 12) throw new TimeoutException(failure);
            source.Poll(); target.Poll();
        }
    }

    /// <summary>Verify crew support follows target ACK before native ability activation.</summary>
    public static void Live(string host)
    {
        using (var source = new LocalSession(CancellationToken.None))
        using (var target = new LocalSession(CancellationToken.None))
        {
            source.Connect(host, "pje", "pje");
            target.Connect(host, "ptt", "ptt");
            Wait(source, target, () => source.Ready && target.Ready &&
                source.World.AvailableAbilities.Contains(44u) &&
                source.World.AvailableAbilities.Contains(125u) &&
                source.World.Players.TryGetValue(target.Player, out ShipActor actor) &&
                actor.HasPosition, "JE support abilities or nearby TT were not advertised.");
            source.InvitePlayer(target.Player);
            Wait(source, target, () => target.World.Confirmation?.Kind == ConfirmationKind.Group,
                "Native group invitation did not reach the target.");
            target.AnswerConfirmation(true);
            Wait(source, target, () => source.World.Group.Members.Any(member =>
                member.Id == target.Player), "Accepted crew membership was not published.");
            source.ActivateAbilityFor(44, target.Player);
            Wait(source, target, () => source.World.Equipment.TargetId == target.Player &&
                target.World.ObjectEffects.Values.Any(effect =>
                    effect.OwnerId == target.Player && effect.Descriptor == 216),
                "Acknowledged Environment Barrier did not reach the selected crew pilot.");
            Position before = target.World.Position;
            source.ActivateAbilityFor(125, target.Player);
            Wait(source, target, () => target.World.Position.Distance(before) > 1000,
                "Acknowledged Teleport Friend did not move the selected crew pilot.");
            Console.WriteLine("PASS: grouped Environment Barrier and Teleport Friend target acknowledgment");
        }
    }
}
