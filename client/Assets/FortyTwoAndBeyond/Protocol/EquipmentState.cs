using System;
using System.Collections.Generic;
using System.IO;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>Server-owned item template information used by the inventory display.</summary>
    public sealed class ItemTemplateInfo
    {
        public readonly uint Id, Type, UseEffect, MaxStack;
        public readonly string Name, ActivationLabel;
        public readonly bool TargetsPeer;
        /// <summary>Store a bounded item name and its server item type.</summary>
        public ItemTemplateInfo(uint id, uint type, string name, string activationLabel,
            bool targetsPeer = false, uint useEffect = 0, uint maxStack = 1)
        { Id = id; Type = type; Name = name; ActivationLabel = activationLabel;
            TargetsPeer = targetsPeer; UseEffect = useEffect; MaxStack = maxStack; }

        /// <returns>The native item template's ID, type and display name.</returns>
        public static ItemTemplateInfo Decode(byte[] body)
        {
            Wire.Require(body, 0, 17);
            uint id = Wire.U32(body, 0, true);
            uint type = Wire.U32(body, 12);
            int count = body[16], offset = 17;
            if (count > 38) throw new InvalidDataException("Item template has too many fields.");
            string name = "";
            for (int i = 0; i < count; i++)
            {
                uint field = Wire.U32(body, offset); offset += 4;
                if (field == 1 || field == 11 || field == 13)
                {
                    int length = Wire.U16(body, offset); offset += 2;
                    string value = Wire.Text(body, offset, length); offset += length;
                    if (field == 1) name = value;
                }
                else { Wire.Require(body, offset, 4); offset += 4; }
            }
            string activationLabel = "";
            bool targetsPeer = false;
            for (int block = 0; block < 2; block++)
            {
                int effects = checked((int)Wire.U32(body, offset, true)); offset += 4;
                if (effects > 64) throw new InvalidDataException("Item effect limit exceeded.");
                for (int i = 0; i < effects; i++)
                {
                    for (int part = 0; part < 3; part++)
                    {
                        int length = Wire.U16(body, offset); offset += 2;
                        if (block == 0 && effects == 1 && part == 1)
                        {
                            string description = Wire.Text(body, offset, length);
                            activationLabel = DeviceEffects.LabelFor(description);
                        }
                        else Wire.Require(body, offset, length);
                        offset += length;
                    }
                    Wire.Require(body, offset, 4); offset += 4;
                    int values = checked((int)Wire.U32(body, offset, true)); offset += 4;
                    if (values > 16) throw new InvalidDataException("Item effect values exceed limit.");
                    Wire.Require(body, offset, values * 4 + 8); offset += values * 4;
                    uint flag1 = Wire.U32(body, offset), flag2 = Wire.U32(body, offset + 4);
                    offset += 8;
                    if (block == 0 && effects == 1 && type == 11 && flag2 == 1)
                        targetsPeer = (activationLabel == "Scan Boost" ||
                            activationLabel == "Reduce Ship Signature") && flag1 == 16 ||
                            (activationLabel == "Recharge Shields" ||
                             activationLabel == "Recharge Reactor") && flag1 == 64;
                }
                if (effects > 0) { Wire.Require(body, offset, 16); offset += 16; }
            }
            // Asset, icon and level are u16; price, stack and shot effect are BE u32.
            Wire.Require(body, offset, 22);
            uint maxStack = Wire.U32(body, offset + 10, true);
            uint useEffect = Wire.U32(body, offset + 14, true);
            offset += 22;
            int nameLength = Wire.U16(body, offset); offset += 2;
            string fullName = Wire.Text(body, offset, nameLength);
            return new ItemTemplateInfo(id, type, fullName.Length > 0 ? fullName : name,
                activationLabel, targetsPeer, useEffect, maxStack);
        }
    }

    /// <summary>A single mutable network-worker item record.</summary>
    public sealed class ItemState
    {
        public uint Template = uint.MaxValue, Quantity, WeaponState, ReadyTick;
        public ulong Price;
        public float Quality, Structure, Range;
        public string Info = "", BuilderName = "";
        public bool Present => Template != uint.MaxValue && Template != uint.MaxValue - 1;
        /// <summary>
        /// Returns:
        ///     uint: The whole stack for launcher ammo, or one installed equipment item.
        /// </summary>
        public uint EquipQuantity(ItemTemplateInfo template)
        { return template?.Type == 10 ? Quantity : 1; }
        /// <returns>A copy for a new server update.</returns>
        public ItemState Copy() => (ItemState)MemberwiseClone();
    }

    /// <summary>Worker-owned cargo, mount and resource values from authoritative AUX packets.</summary>
    public sealed class EquipmentState
    {
        public ItemState[] Cargo = new ItemState[40], Mounted = new ItemState[20], Ammo = new ItemState[20], Vault = new ItemState[96];
        public uint CargoCapacity;
        public float Energy = -1, Shield = -1, Hull = -1, MaxEnergy = -1, MaxShield = -1, MaxHull = -1;
        public uint WeaponMounts, DeviceMounts, TargetId;
        public bool Firing;

        /// <summary>
        /// Returns:
        ///     int: An empty unlocked cargo slot, or -1 when capacity is unknown or full.
        /// </summary>
        public int FirstFreeCargoSlot()
        {
            for (int slot = 0; slot < Math.Min(CargoCapacity, (uint)Cargo.Length); slot++)
                if (Cargo[slot]?.Present != true) return slot;
            return -1;
        }
    }

    /// <summary>Stable inventory row copied for the Unity thread.</summary>
    public sealed class ItemView
    {
        public readonly int Slot;
        public readonly uint Template, Quantity, Type, WeaponState, ReadyTick, UseEffect, MaxStack;
        public readonly ulong Price;
        public readonly float Quality, Structure, Range;
        public readonly float CooldownSeconds;
        public readonly string Name, Info, ActivationLabel, BuilderName;
        public readonly bool TargetsPeer;
        public bool Cooling => Type >= 14 && Type <= 16 && (WeaponState & 0x80) != 0;
        public bool OutOfAmmo => (Type == 15 || Type == 16) && (WeaponState & 8) != 0;
        /// <summary>Resolve a worker item against the known server catalog.</summary>
        public ItemView(int slot, ItemState item, IReadOnlyDictionary<uint, ItemTemplateInfo> catalog,
            uint? estimatedServerTick = null)
        {
            Slot = slot; Template = item.Template; Quantity = item.Quantity;
            Quality = item.Quality; Structure = item.Structure; Range = item.Range;
            WeaponState = item.WeaponState; ReadyTick = item.ReadyTick;
            Price = item.Price; Info = item.Info; BuilderName = item.BuilderName;
            if (catalog.TryGetValue(item.Template, out ItemTemplateInfo template))
            { Name = template.Name; Type = template.Type; ActivationLabel = template.ActivationLabel;
                TargetsPeer = template.TargetsPeer; UseEffect = template.UseEffect; MaxStack = template.MaxStack; }
            else { Name = "Item #" + item.Template; Type = 0; ActivationLabel = "";
                TargetsPeer = false; UseEffect = MaxStack = 0; }
            if (Cooling && ReadyTick != 0 && estimatedServerTick.HasValue)
            {
                int remainingMs = unchecked((int)(ReadyTick - estimatedServerTick.Value));
                CooldownSeconds = Math.Max(0, Math.Min(remainingMs / 1000f, 600));
            }
        }
    }
}
