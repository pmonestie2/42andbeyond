using System;
using System.Linq;
using FortyTwoAndBeyond.Client;
using FortyTwoAndBeyond.Protocol;

internal static class ServerHistoryChecks
{
    /// <summary>Check five-entry ordering, saved reloads, deduplication and invalid input retention.</summary>
    public static void Check()
    {
        var history = new ServerHistory(new[] { "", "bad host" });
        Require(history.Entries.SequenceEqual(new[] { ServerEndpoint.DefaultHost }), "Missing local default.");
        for (int index = 1; index <= 6; index++) history.Remember("server" + index + ".example.com");
        Require(history.Entries.Count == 5 && history.Entries[0] == "server6.example.com" &&
            history.Entries[4] == "server2.example.com", "History did not evict the oldest server.");
        history.Remember(" SERVER4.EXAMPLE.COM ");
        Require(history.Entries.Count == 5 && history.Entries[0] == "SERVER4.EXAMPLE.COM" &&
            history.Entries.Count(host => host.Equals("server4.example.com", StringComparison.OrdinalIgnoreCase)) == 1,
            "Hostname duplicates were not promoted and trimmed.");
        var restored = new ServerHistory(history.Entries);
        Require(restored.Entries.SequenceEqual(history.Entries), "Reload changed saved server order.");
        string[] before = restored.Entries.ToArray();
        try { restored.Remember("https://invalid.example.com"); }
        catch (ArgumentException) { }
        Require(restored.Entries.SequenceEqual(before), "Invalid input changed saved history.");
        var duplicateSaved = new ServerHistory(new[] { "prod.example.com", "PROD.EXAMPLE.COM", "216.219.87.147" });
        Require(duplicateSaved.Entries.Count == 2, "Saved duplicates survived restore.");
        Console.WriteLine("PASS: five recent servers, ordering, deduplication, persistence reload and invalid input");
    }

    /// <summary>Fail with a specific history regression message.</summary>
    private static void Require(bool condition, string message)
    { if (!condition) throw new Exception(message); }
}
