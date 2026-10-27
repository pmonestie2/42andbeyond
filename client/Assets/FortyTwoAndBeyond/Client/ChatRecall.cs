using System.Collections.Generic;

namespace FortyTwoAndBeyond.Client
{
    /// <summary>Recall this client's sent messages and commands while preserving the current draft.</summary>
    public sealed class ChatRecall
    {
        private readonly List<string> entries = new List<string>();
        private int position;
        private string draft = "";

        /// <summary>Start recall at the newest sent line when a composer opens.</summary>
        public void Begin() { position = entries.Count; draft = ""; }

        /// <summary>Remember up to one hundred sent lines, merging consecutive duplicates.</summary>
        public void Remember(string text)
        {
            text = text.Trim();
            if (text.Length == 0) return;
            if (entries.Count == 0 || entries[entries.Count - 1] != text) entries.Add(text);
            if (entries.Count > 100) entries.RemoveAt(0);
            position = entries.Count; draft = "";
        }

        /// <summary>
        /// Returns:
        ///     string: The previous sent line, or the current draft when history is empty.
        /// </summary>
        public string Previous(string current)
        {
            if (entries.Count == 0) return current;
            if (position == entries.Count) draft = current;
            if (position > 0) position--;
            return entries[position];
        }

        /// <summary>
        /// Returns:
        ///     string: The next sent line, restoring the unsent draft after the newest entry.
        /// </summary>
        public string Next(string current)
        {
            if (position >= entries.Count) return current;
            position++;
            return position == entries.Count ? draft : entries[position];
        }
    }
}
