using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>Local public navigation knowledge; searching and plotting never contact or modify a server.</summary>
    public sealed class GalaxyNavigationCatalog
    {
        private const uint PageSize = 64;
        private readonly GalaxyDestination[] entries;
        private readonly Dictionary<ulong, GalaxyDestination> destinations;
        private readonly Dictionary<uint, GalaxyDestination> stations;
        private readonly Dictionary<uint, string> sectors;
        public static GalaxyNavigationCatalog Empty { get; } = new GalaxyNavigationCatalog(Array.Empty<GalaxyDestination>());
        public int Count => entries.Length;

        /// <summary>Own sorted local destination knowledge and derive station launch edges.</summary>
        private GalaxyNavigationCatalog(GalaxyDestination[] values)
        {
            entries = values.OrderBy(value => value.SystemName, StringComparer.Ordinal)
                .ThenBy(value => value.SectorName, StringComparer.Ordinal).ThenBy(value => value.Name, StringComparer.Ordinal)
                .ThenBy(value => value.Sector).ThenBy(value => value.Id).ToArray();
            destinations = entries.ToDictionary(value => Key(value.Sector, value.Id));
            stations = new Dictionary<uint, GalaxyDestination>(); sectors = new Dictionary<uint, string>();
            foreach (GalaxyDestination value in entries)
            {
                sectors[value.Sector] = value.SectorName;
                if (value.Kind == 12 && value.DestinationSector != 0)
                { stations[value.DestinationSector] = value; sectors[value.DestinationSector] = value.Name; }
            }
        }

        /// <summary>
        /// Returns:
        ///     GalaxyNavigationCatalog: Owned GXY1 local knowledge, or an empty catalog when no resource is supplied.
        /// Raises:
        ///     InvalidDataException: The resource is malformed, oversized, duplicated, or contains invalid locations.
        /// </summary>
        public static GalaxyNavigationCatalog Decode(byte[] data)
        {
            if (data == null || data.Length == 0) return Empty;
            if (data.Length < 8 || data.Length > 2 * 1024 * 1024 || data[0] != 'G' || data[1] != 'X' || data[2] != 'Y' || data[3] != '1')
                throw new InvalidDataException("Invalid local navigation catalog.");
            try
            {
                using (var stream = new MemoryStream(data, writable: false))
                using (var reader = new BinaryReader(stream))
                {
                    reader.ReadUInt32(); uint count = reader.ReadUInt32();
                    if (count > 10000) throw new InvalidDataException("Navigation catalog exceeds its entry bound.");
                    var values = new GalaxyDestination[count]; var identities = new HashSet<ulong>();
                    for (int index = 0; index < values.Length; index++)
                    {
                        uint sector = reader.ReadUInt32(), id = reader.ReadUInt32(), kind = reader.ReadUInt32(), destination = reader.ReadUInt32();
                        float x = reader.ReadSingle(), y = reader.ReadSingle(), z = reader.ReadSingle(), radius = reader.ReadSingle();
                        if (sector == 0 || id == 0 || kind != 11 && kind != 12 || !identities.Add(Key(sector, id)) ||
                            !Coordinate(x) || !Coordinate(y) || !Coordinate(z) || float.IsNaN(radius) || float.IsInfinity(radius) || radius < 0 || radius > 1e7)
                            throw new InvalidDataException("Invalid known navigation destination.");
                        string name = Label(reader), sectorName = Label(reader), systemName = Label(reader);
                        if (name.Length == 0 || sectorName.Length == 0) throw new InvalidDataException("Unnamed navigation destination.");
                        values[index] = new GalaxyDestination(sector, id, kind, destination, new Position(x, y, z), radius, name, sectorName, systemName);
                    }
                    if (stream.Position != stream.Length) throw new InvalidDataException("Trailing local navigation data.");
                    return new GalaxyNavigationCatalog(values);
                }
            }
            catch (EndOfStreamException error) { throw new InvalidDataException("Truncated local navigation catalog.", error); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid navigation UTF-8.", error); }
        }

        /// <summary>
        /// Returns:
        ///     GalaxyCatalogPage: One stable local page filtered to gates (1), stations (2), or both (3).
        /// Raises:
        ///     ArgumentOutOfRangeException: The filter has no supported gate/station selection.
        /// </summary>
        public GalaxyCatalogPage Query(uint requestId, uint page, uint flags, string query)
        {
            if (flags < 1 || flags > 3) throw new ArgumentOutOfRangeException(nameof(flags));
            query = (query ?? "").Trim();
            GalaxyDestination[] matches = entries.Where(value => (flags & (value.Kind == 11 ? 1u : 2u)) != 0 &&
                (query.Length == 0 || Contains(value.Name, query) || Contains(value.SectorName, query) || Contains(value.SystemName, query))).ToArray();
            uint last = matches.Length == 0 ? 0 : ((uint)matches.Length - 1) / PageSize;
            page = Math.Min(page, last);
            return new GalaxyCatalogPage(requestId, page, (uint)matches.Length, PageSize,
                matches.Skip((int)(page * PageSize)).Take((int)PageSize).ToArray());
        }

        /// <summary>
        /// Returns:
        ///     GalaxyRoutePlan: A shortest known route ending with the chosen crossing/dock, or a readable local planning failure.
        /// </summary>
        public GalaxyRoutePlan Plan(uint startSector, uint targetSector, uint targetObject,
            IReadOnlyDictionary<uint, MapObject> currentObjects = null)
        {
            if (!destinations.TryGetValue(Key(targetSector, targetObject), out GalaxyDestination target))
                return Failure(startSector, targetSector, targetObject, "This destination is not in the local known-gate catalog.");
            if (target.DestinationSector == 0)
                return Failure(startSector, targetSector, targetObject, target.Kind == 12 ? "This station interior is unavailable." : "This gate has no known working destination.");
            if (target.Kind == 12 && startSector == target.DestinationSector)
                return new GalaxyRoutePlan(startSector, targetSector, targetObject, startSector, true, "", Array.Empty<GalaxyRouteLeg>());
            if (targetSector == startSector && !Observed(target, currentObjects))
                return Failure(startSector, targetSector, targetObject, "This destination is absent from the current sector's navigation data.");
            var previous = new Dictionary<uint, GalaxyRouteLeg> { [startSector] = null };
            var queue = new Queue<uint>(); queue.Enqueue(startSector);
            while (queue.Count > 0)
            {
                uint sector = queue.Dequeue();
                if (sector == targetSector) break;
                foreach (GalaxyRouteLeg edge in Edges(sector, startSector, currentObjects))
                    if (!previous.ContainsKey(edge.DestinationSector))
                    { previous.Add(edge.DestinationSector, edge); queue.Enqueue(edge.DestinationSector); }
            }
            if (!previous.ContainsKey(targetSector)) return Failure(startSector, targetSector, targetObject, "No route through known gates reaches this destination.");
            var legs = new List<GalaxyRouteLeg>(); uint current = targetSector;
            while (current != startSector) { GalaxyRouteLeg edge = previous[current]; legs.Add(edge); current = edge.SourceSector; }
            legs.Reverse();
            Position approach = ObservedPosition(target, targetSector == startSector ? currentObjects : null);
            legs.Add(new GalaxyRouteLeg(target.Kind == 12 ? 2u : 0u, targetSector, target.Id, target.DestinationSector,
                approach, target.Radius, target.SectorName, SectorName(target.DestinationSector)));
            if (legs.Count > 256) return Failure(startSector, targetSector, targetObject, "This known route has too many transitions.");
            return new GalaxyRoutePlan(startSector, targetSector, targetObject, target.DestinationSector, true, "", legs.ToArray());
        }

        /// <summary>Returns:
        ///     IEnumerable<GalaxyRouteLeg>: Deterministic known gate crossings or a station's launch edge.
        /// </summary>
        private IEnumerable<GalaxyRouteLeg> Edges(uint sector, uint start, IReadOnlyDictionary<uint, MapObject> observed)
        {
            if (stations.TryGetValue(sector, out GalaxyDestination station))
            {
                yield return new GalaxyRouteLeg(1, sector, sector, station.Sector, new Position(0, 0, 0), 0, station.Name, station.SectorName);
                yield break;
            }
            foreach (GalaxyDestination entry in entries.Where(value => value.Sector == sector && value.Kind == 11 && value.DestinationSector != 0)
                .OrderBy(value => value.DestinationSector).ThenBy(value => value.Id))
            {
                IReadOnlyDictionary<uint, MapObject> actual = sector == start ? observed : null;
                if (!Observed(entry, actual)) continue;
                yield return new GalaxyRouteLeg(0, sector, entry.Id, entry.DestinationSector, ObservedPosition(entry, actual), entry.Radius,
                    entry.SectorName, SectorName(entry.DestinationSector));
            }
        }

        /// <summary>Returns:
        ///     bool: Whether a known entry is present with its expected type in an optional observed current map.
        /// </summary>
        private static bool Observed(GalaxyDestination entry, IReadOnlyDictionary<uint, MapObject> observed)
        { return observed == null || observed.TryGetValue(entry.Id, out MapObject value) && value.Kind == entry.Kind; }

        /// <summary>Returns:
        ///     Position: The observed current position when available, otherwise the locally known approach point.
        /// </summary>
        private static Position ObservedPosition(GalaxyDestination entry, IReadOnlyDictionary<uint, MapObject> observed)
        { return observed != null && observed.TryGetValue(entry.Id, out MapObject value) ? value.Position : entry.Position; }

        /// <summary>Returns:
        ///     string: A known space/station label, with an honest ID fallback for an unnamed arrival sector.
        /// </summary>
        private string SectorName(uint id) { return sectors.TryGetValue(id, out string name) ? name : "Sector " + id; }
        /// <summary>Returns:
        ///     GalaxyRoutePlan: An immutable local failure without scheduling any movement.
        /// </summary>
        private static GalaxyRoutePlan Failure(uint start, uint sector, uint target, string error)
        { return new GalaxyRoutePlan(start, sector, target, 0, false, error, Array.Empty<GalaxyRouteLeg>()); }
        /// <summary>Returns:
        ///     ulong: A collision-free sector/object pair.
        /// </summary>
        private static ulong Key(uint sector, uint id) { return ((ulong)sector << 32) | id; }
        /// <summary>Returns:
        ///     bool: Whether a coordinate is finite and within supported space bounds.
        /// </summary>
        private static bool Coordinate(float value) { return !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) < 1e9; }
        /// <summary>Returns:
        ///     bool: Whether a display field matches the local case-insensitive search.
        /// </summary>
        private static bool Contains(string value, string query) { return value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0; }
        /// <summary>Returns:
        ///     string: One bounded strict UTF-8 label.
        /// </summary>
        private static string Label(BinaryReader reader)
        {
            int size = reader.ReadUInt16();
            if (size > 512) throw new InvalidDataException("Navigation label exceeds 512 bytes.");
            byte[] data = reader.ReadBytes(size);
            if (data.Length != size) throw new EndOfStreamException();
            string result = new UTF8Encoding(false, true).GetString(data);
            if (result.IndexOf('\0') >= 0) throw new InvalidDataException("Navigation label contains a null character.");
            return result;
        }
    }
}
