namespace FortyTwoAndBeyond.Client
{
    /// <summary>One server-advertised action, organized by skill family, effect and trained level.</summary>
    public sealed class SystemChoice
    {
        public readonly int Id, Skill, Level;
        public readonly string Family, Effect, Name;
        public readonly bool Selected;
        /// <summary>Freeze a selectable system variant without activating it.</summary>
        public SystemChoice(int id, int skill, int level, string family, string effect, string name, bool selected)
        { Id = id; Skill = skill; Level = level; Family = family; Effect = effect; Name = name; Selected = selected; }
    }

    /// <summary>Authoritative active/recovery presentation for an assigned system.</summary>
    public sealed class SystemActivity
    {
        public readonly bool Active, Charging;
        public readonly float Remaining, CoveredFraction;
        /// <summary>Freeze a system's current lease and recovery display.</summary>
        public SystemActivity(bool active, bool charging, float remaining, float coveredFraction)
        { Active = active; Charging = charging; Remaining = remaining; CoveredFraction = coveredFraction; }
    }
}
