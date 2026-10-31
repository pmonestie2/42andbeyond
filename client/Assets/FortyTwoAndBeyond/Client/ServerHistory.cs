using System;
using System.Collections.Generic;
using FortyTwoAndBeyond.Protocol;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Keep five distinct server names in most-recent-first order, without credentials.</summary>
    public sealed class ServerHistory
    {
        public const int Capacity = 5;
        private readonly List<string> entries = new List<string>();
        public IReadOnlyList<string> Entries { get; }

        /// <summary>Restore valid saved entries, preserving their order and the local default.</summary>
        public ServerHistory(IEnumerable<string> saved)
        {
            foreach (string host in saved)
            {
                try
                {
                    string normalized = ServerEndpoint.NormalizeHost(host);
                    if (!entries.Exists(entry => string.Equals(entry, normalized, StringComparison.OrdinalIgnoreCase)))
                        entries.Add(normalized);
                    if (entries.Count == Capacity) break;
                }
                catch (ArgumentException) { }
            }
            if (entries.Count == 0) entries.Add(ServerEndpoint.DefaultHost);
            Entries = entries.AsReadOnly();
        }

        /// <summary>Move a submitted server to the front, dropping the oldest beyond five.</summary>
        /// Raises:
        ///     ArgumentException: The submitted server name is invalid.
        public void Remember(string host)
        {
            string normalized = ServerEndpoint.NormalizeHost(host);
            entries.RemoveAll(entry => string.Equals(entry, normalized, StringComparison.OrdinalIgnoreCase));
            entries.Insert(0, normalized);
            if (entries.Count > Capacity) entries.RemoveRange(Capacity, entries.Count - Capacity);
        }
    }
}
