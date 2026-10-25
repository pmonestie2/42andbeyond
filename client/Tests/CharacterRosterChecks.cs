using System;
using System.Diagnostics;
using System.Threading;
using FortyTwoAndBeyond.Protocol;

internal static class CharacterRosterChecks
{
    /// <summary>Read a disposable level-zero pilot without entering or mutating its session.</summary>
    public static void Live(string address)
    {
        using (var picker = new CharacterPickerWorker(address, "rookie", "rookie"))
        {
            var clock = Stopwatch.StartNew();
            while (picker.Characters == null && picker.Error == null &&
                clock.Elapsed.TotalSeconds < 12) Thread.Sleep(20);
            if (picker.Error != null) throw new Exception(picker.Error);
            CharacterInfo[] pilots = picker.Characters;
            if (pilots == null || pilots.Length != 1 || pilots[0].Name != "Rookie" ||
                pilots[0].ClassName != "Terran Scout" || pilots[0].CombatLevel != 0 ||
                pilots[0].TradeLevel != 0 || pilots[0].ExploreLevel != 0)
                throw new Exception("Native progression roster did not report a level-zero Scout.");
        }
        Console.WriteLine("PASS: native level-zero Scout roster class and track levels");
    }
}
