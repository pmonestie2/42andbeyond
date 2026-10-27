using System;
using System.Collections.Generic;
using System.IO;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>One authoritative station vendor quote for a component.</summary>
    public sealed class ComponentMarketSource
    {
        public readonly uint Station, Npc, VendorSlot;
        public readonly ulong BuyPrice, SellPrice;
        public readonly string StationName, SectorName, VendorName;

        /// <summary>Keep location, vendor identity, and negotiated unit prices together.</summary>
        public ComponentMarketSource(uint station, uint npc, uint vendorSlot, ulong buyPrice,
            ulong sellPrice, string stationName, string sectorName, string vendorName)
        {
            Station = station; Npc = npc; VendorSlot = vendorSlot; BuyPrice = buyPrice;
            SellPrice = sellPrice; StationName = stationName; SectorName = sectorName; VendorName = vendorName;
        }
    }

    /// <summary>Immutable market response shared by the network worker and station overlay.</summary>
    public sealed class ComponentMarketView
    {
        public readonly uint Template, CurrentStation;
        public readonly bool DevEnabled;
        public readonly IReadOnlyList<ComponentMarketSource> Sources;

        /// <summary>Copy a bounded market response into immutable display data.</summary>
        private ComponentMarketView(uint template, uint currentStation, bool devEnabled,
            ComponentMarketSource[] sources)
        {
            Template = template; CurrentStation = currentStation; DevEnabled = devEnabled;
            Sources = Array.AsReadOnly(sources);
        }

        /// <summary>
        /// Returns:
        ///     ComponentMarketView: Validated server quotes and station destinations.
        /// Raises:
        ///     InvalidDataException: The payload is truncated, oversized, or inconsistent.
        /// </summary>
        public static ComponentMarketView Decode(byte[] body)
        {
            Wire.Require(body, 0, 16);
            uint template = Wire.U32(body, 0), currentStation = Wire.U32(body, 4);
            uint dev = Wire.U32(body, 8), count = Wire.U32(body, 12);
            if (template == 0 || dev > 1 || count > 128)
                throw new InvalidDataException("Invalid component market response.");
            int offset = 16;
            var sources = new ComponentMarketSource[count];
            var identities = new HashSet<ulong>();
            for (int index = 0; index < sources.Length; index++)
            {
                Wire.Require(body, offset, 28);
                uint station = Wire.U32(body, offset), npc = Wire.U32(body, offset + 4);
                ulong buyPrice = Wire.U32(body, offset + 8) | ((ulong)Wire.U32(body, offset + 12) << 32);
                ulong sellPrice = Wire.U32(body, offset + 16) | ((ulong)Wire.U32(body, offset + 20) << 32);
                uint slot = Wire.U32(body, offset + 24); offset += 28;
                if (station == 0 || npc == 0 || !identities.Add(((ulong)station << 32) | npc) ||
                    buyPrice == 0 && sellPrice == 0 || buyPrice > 0 && slot >= 128)
                    throw new InvalidDataException("Invalid component vendor quote.");
                string stationName = ReadName(body, ref offset);
                string sectorName = ReadName(body, ref offset);
                string vendorName = ReadName(body, ref offset);
                sources[index] = new ComponentMarketSource(station, npc, slot, buyPrice,
                    sellPrice, stationName, sectorName, vendorName);
            }
            if (offset != body.Length) throw new InvalidDataException("Trailing component market data.");
            return new ComponentMarketView(template, currentStation, dev == 1, sources);
        }

        /// <summary>
        /// Returns:
        ///     string: One bounded UTF-8 display name from the response.
        /// </summary>
        private static string ReadName(byte[] body, ref int offset)
        {
            int length = Wire.U16(body, offset); offset += 2;
            if (length > 512) throw new InvalidDataException("Component market name is too long.");
            string name = Wire.Text(body, offset, length); offset += length;
            return name;
        }
    }
}
