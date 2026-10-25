using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using FortyTwoAndBeyond.Protocol;

internal static class SkillTrainingChecks
{
    /// <summary>Poll a disposable cadet until the server publishes its training state.</summary>
    private static void Wait(LocalSession session, Func<bool> ready, string failure)
    {
        var deadline = Stopwatch.StartNew();
        while (!ready())
        {
            if (deadline.Elapsed.TotalSeconds > 12) throw new TimeoutException(failure);
            session.Poll();
        }
    }

    /// <summary>Buy a rank or verify its durable state after server replacement.</summary>
    public static void Live(string host, bool resumed)
    {
        using (var session = new LocalSession(CancellationToken.None))
        {
            session.Connect(host, "cadet", "cadet");
            Wait(session, () => session.Ready && session.World.Experience != null &&
                session.World.Skills.Any(skill => skill.Id == 1),
                "Cadet skill AUX was not published.");
            SkillRank beam = session.World.Skills.First(skill => skill.Id == 1);
            if (resumed)
            {
                if (beam.Rank != 2 || session.World.Experience.AvailableSkillPoints != 0)
                    throw new Exception("Durable Beam training did not survive server replacement.");
                Console.WriteLine("PASS: native Beam rank 2 and spent points survived restart");
                return;
            }
            if (beam.Rank != 1 || !beam.CanTrain || beam.Cost != 1 ||
                session.World.Experience.AvailableSkillPoints != 1)
                throw new Exception("Cadet's advertised rank, cost, or points were incorrect.");
            session.TrainSkill(1);
            Wait(session, () => session.World.Skills.Any(skill => skill.Id == 1 && skill.Rank == 2) &&
                session.World.Experience.AvailableSkillPoints == 0,
                "Native 0x57 did not publish rank 2 and zero remaining points.");
            Console.WriteLine("PASS: native 0x57 Beam purchase and authoritative rank/point refresh");
        }
    }
}
