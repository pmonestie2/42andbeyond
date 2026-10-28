using System;
using System.Collections.Generic;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>One gate or station listed in the client's known galaxy map.</summary>
    public sealed class GalaxyDestination
    {
        public readonly uint Sector, Id, Kind, DestinationSector;
        public readonly Position Position;
        public readonly float Radius;
        public readonly string Name, SectorName, SystemName;
        /// <summary>Keep mapped destination identity and location together.</summary>
        public GalaxyDestination(uint sector, uint id, uint kind, uint destinationSector, Position position,
            float radius, string name, string sectorName, string systemName)
        { Sector = sector; Id = id; Kind = kind; DestinationSector = destinationSector; Position = position;
            Radius = radius; Name = name; SectorName = sectorName; SystemName = systemName; }
    }

    /// <summary>One immutable, bounded galaxy search result.</summary>
    public sealed class GalaxyCatalogPage
    {
        public readonly uint RequestId, Page, Total, PageSize;
        public readonly IReadOnlyList<GalaxyDestination> Entries;
        /// <summary>Retain one owned search page without exposing worker collections.</summary>
        internal GalaxyCatalogPage(uint requestId, uint page, uint total, uint pageSize, GalaxyDestination[] entries)
        { RequestId = requestId; Page = page; Total = total; PageSize = pageSize; Entries = Array.AsReadOnly(entries); }
    }

    /// <summary>One real gate, station launch, or docking step.</summary>
    public sealed class GalaxyRouteLeg
    {
        public readonly uint Kind, SourceSector, Object, DestinationSector;
        public readonly Position Position;
        public readonly float Radius;
        public readonly string SourceName, DestinationName;
        /// <summary>Store one known-map route edge and its approach point.</summary>
        public GalaxyRouteLeg(uint kind, uint sourceSector, uint objectId, uint destinationSector,
            Position position, float radius, string sourceName, string destinationName)
        { Kind = kind; SourceSector = sourceSector; Object = objectId; DestinationSector = destinationSector;
            Position = position; Radius = radius; SourceName = sourceName; DestinationName = destinationName; }
    }

    /// <summary>Immutable route preview, independent of any currently running journey.</summary>
    public sealed class GalaxyRoutePlan
    {
        public readonly uint StartSector, TargetSector, TargetObject, ArrivalSector;
        public readonly bool Success;
        public readonly string Error;
        public readonly IReadOnlyList<GalaxyRouteLeg> Legs;
        /// <summary>Own a validated route and its destination identity.</summary>
        public GalaxyRoutePlan(uint startSector, uint targetSector, uint targetObject, uint arrivalSector,
            bool success, string error, GalaxyRouteLeg[] legs)
        { StartSector = startSector; TargetSector = targetSector; TargetObject = targetObject; ArrivalSector = arrivalSector;
            Success = success; Error = error; Legs = Array.AsReadOnly((GalaxyRouteLeg[])legs.Clone()); }
    }

    /// <summary>Worker-owned journey progress copied into each display snapshot.</summary>
    public sealed class GalaxyTravelView
    {
        public readonly bool Active, Completed;
        public readonly int LegIndex;
        public readonly string Status;
        public readonly uint TargetSector, TargetObject;
        /// <summary>Publish progress without exposing the pilot's mutable state.</summary>
        public GalaxyTravelView(bool active, bool completed, int legIndex, string status,
            uint targetSector = 0, uint targetObject = 0)
        { Active = active; Completed = completed; LegIndex = legIndex; Status = status;
            TargetSector = targetSector; TargetObject = targetObject; }
    }

}
