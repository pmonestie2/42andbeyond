using System;
using System.Collections.Generic;
using System.IO;

namespace FortyTwoAndBeyond.Protocol
{
    public sealed class GroupMember
    {
        public readonly uint Id;
        public readonly string Name;
        public readonly bool Formed;
        public readonly uint FormationSlot;
        /// <summary>Store one authoritative group member and formation status.</summary>
        public GroupMember(uint id, string name, bool formed = false, uint formationSlot = uint.MaxValue)
        { Id = id; Name = name; Formed = formed; FormationSlot = formationSlot; }
    }

    public sealed class GroupState
    {
        public readonly bool IsLeader, Formed;
        public readonly string Formation;
        public readonly GroupMember[] Members;
        public bool Joined => IsLeader || Members.Length > 0;
        /// <summary>Store the local role and other members of a server group.</summary>
        public GroupState(bool isLeader, GroupMember[] members, string formation = "", bool formed = false)
        { IsLeader = isLeader; Members = members; Formation = formation; Formed = formed; }
    }

    /// <summary>One native cast progress revision for the character.</summary>
    public sealed class AbilityProgress
    {
        public readonly uint Id, DurationMs;
        /// <summary>Keep the advertised ability and remaining cast duration together.</summary>
        public AbilityProgress(uint id, uint durationMs) { Id = id; DurationMs = durationMs; }
    }

    /// <summary>One server-owned skill cooldown revision.</summary>
    public sealed class SkillCooldown
    {
        public readonly uint SkillId, DurationMs;
        /// <summary>Keep the skill identity with its recovery duration.</summary>
        public SkillCooldown(uint skillId, uint durationMs)
        { SkillId = skillId; DurationMs = durationMs; }
    }

    /// <summary>Read the supported prefixes of native AUX records without guessing unknown layouts.</summary>
    internal sealed partial class AuxState
    {
        private readonly byte[] body;
        private int offset = 7;
        private sealed class Flags
        {
            public readonly bool[] Present, Default;
            /// <summary>Allocate a bounded native record mask.</summary>
            public Flags(int count) { Present = new bool[count]; Default = new bool[count]; }
        }
        /// <summary>Validate the envelope before reading nested fields.</summary>
        private AuxState(byte[] body, bool shipEnvelope = false)
        {
            this.body = body;
            ushort declared = Wire.U16(body, 4);
            bool contrail = shipEnvelope && body.Length == 19 && declared == 19 &&
                body[7] == 2 && Wire.U32(body, 8) == 0 &&
                body[12] == 0 && body[13] == 4 && body[14] == 0;
            // Production peer snapshots can append an eight-byte suffix outside
            // the declared AUX record. Keep field reads inside that record.
            if (shipEnvelope && declared + 14 == body.Length)
                this.body = Wire.Slice(body, 0, body.Length - 8);
            if (declared != this.body.Length - 6 && !contrail || body[6] != 1)
                throw new InvalidDataException("Invalid AUX envelope.");
        }
        /// <returns>The next native field-presence mask.</returns>
        private Flags Mask(int count)
        {
            Wire.Require(body, offset, 1);
            var flags = new Flags(count);
            if (body[offset] == 5) { offset++; return flags; }
            if (body[offset] == 3)
            {
                offset++;
                for (int field = 0; field < count; field++) flags.Present[field] = true;
                return flags;
            }
            int control = body[offset] & 15;
            if (control != 2 && control != 6) throw new InvalidDataException("Invalid AUX flags for " + count + " fields at byte " + offset + " of " + body.Length + " (value " + body[offset] + ").");
            int length = (4 + count * (control == 6 ? 2 : 1) + 7) / 8;
            Wire.Require(body, offset, length);
            for (int i = 0; i < count; i++)
            {
                int bit = 4 + i;
                flags.Present[i] = (body[offset + bit / 8] & (1 << (bit % 8))) != 0;
                bit += count;
                // Native containers retain their second bit when populated.
                // Expose a reset only when the corresponding value is absent.
                flags.Default[i] = !flags.Present[i] && control == 6 &&
                    (body[offset + bit / 8] & (1 << (bit % 8))) != 0;
            }
            offset += length; return flags;
        }
        /// <returns>The next length-prefixed UTF-8 value.</returns>
        private string Text()
        {
            int length = Wire.U16(body, offset); offset += 2;
            string value = Wire.Text(body, offset, length); offset += length; return value;
        }
        /// <returns>The next little-endian integer.</returns>
        private uint UInt() { uint value = Wire.U32(body, offset); offset += 4; return value; }
        /// <returns>The next finite resource value.</returns>
        private float Float() { float value = Wire.Float(body, offset); offset += 4; return value; }
        /// <returns>The next byte.</returns>
        private byte Byte() { Wire.Require(body, offset, 1); return body[offset++]; }
        /// <returns>The current fraction from a three-field resource record.</returns>
        private float Percent(float previous = -1)
        {
            Wire.Require(body, offset, 1);
            bool reset = body[offset] == 5;
            Flags flags = Mask(3); float result = reset || flags.Default[2] ? 0 : previous;
            for (int i = 0; i < 3; i++) if (flags.Present[i])
            { if (i == 0) UInt(); else { float value = Float(); if (i == 2) result = value; } }
            return result;
        }
        /// <summary>
        /// Returns:
        ///     TimedPercentState: The native resource curve merged with omitted fields from earlier AUX revisions.
        /// </summary>
        private TimedPercentState TimedPercent(TimedPercentState previous)
        {
            Wire.Require(body, offset, 1);
            if (body[offset] == 5)
            {
                offset++;
                return new TimedPercentState(true, 0, 0, 0,
                    System.Diagnostics.Stopwatch.GetTimestamp());
            }
            Flags flags = Mask(3);
            uint endTick = 0;
            float rate = 0, startValue = 0;
            if (flags.Present[0]) endTick = UInt();
            if (flags.Present[1]) rate = Float();
            if (flags.Present[2]) startValue = Float();
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            return previous.Merge(flags.Present[0] || flags.Default[0], endTick,
                flags.Present[1] || flags.Default[1], rate,
                flags.Present[2] || flags.Default[2], startValue, null, now);
        }
        /// <returns>An updated contact with authoritative name, shield, hull and combat-level fields.</returns>
        public static ShipActor ReadShip(byte[] body, ShipActor ship)
        {
            var reader = new AuxState(body, shipEnvelope: true); Flags flags = reader.Mask(58);
            string name = ship.Name; float shield = ship.Shield, hull = ship.Hull, maxHull = ship.MaxHull, maxShield = ship.MaxShield;
            uint? combatLevel = ship.CombatLevel;
            if (flags.Default[26] && !flags.Present[26]) combatLevel = null;
            for (int i = 0; i <= 26; i++)
            {
                if ((i == 4 || i == 6) && (flags.Present[i] || flags.Default[i]))
                { float value = reader.Percent(i == 6 ? shield : -1); if (i == 6 && value >= 0) shield = value; continue; }
                if (!flags.Present[i]) continue;
                if (i >= 0 && i <= 3) { string value = reader.Text(); if (i == 0 && value.Length > 0) name = value; }
                else if (i >= 5 && i <= 15)
                { float value = reader.Float(); if (i == 7) maxShield = value; if (i == 8) hull = value; if (i == 9) maxHull = value; }
                else if (i >= 16 && i <= 25) reader.Byte();
                else if (i == 26) combatLevel = reader.UInt();
                else return ship; // Fields 2–3 are not emitted by this server; never infer their width.
            }
            return new ShipActor(ship.Id, ship.Asset, ship.Scale, name, ship.Position, ship.Orientation,
                ship.HasPosition, ship.IsPlayer, ship.Hostile, shield, hull, maxHull, maxShield, combatLevel);
        }
        /// <summary>
        /// Returns:
        ///     WarpState: An authoritative warp revision, or null when this diff omits warp fields.
        /// </summary>
        internal static WarpState ReadOwn(byte[] body, EquipmentState equipment,
            TradeOfferState previousTrade, out TimedDeviceBuff[] buffRevisions,
            out TradeOfferRevision tradeRevision, WarpState previousWarp = null)
        {
            var reader = new AuxState(body, shipEnvelope: true); Flags fields = reader.Mask(58);
            uint? privateWarp = null, globalWarp = null;
            bool moneyPresent = false, slotsPresent = false;
            ulong tradeMoney = 0;
            ItemState[] tradeSlots = new ItemState[6];
            if (previousTrade != null)
                for (int slot = 0; slot < tradeSlots.Length; slot++)
                    tradeSlots[slot] = previousTrade.Slots[slot]?.Copy();
            buffRevisions = new TimedDeviceBuff[16];
            tradeRevision = null;
            for (int i = 0; i <= 45; i++)
            {
                if (!fields.Present[i] && fields.Default[i] && (i == 4 || i == 6 || i >= 35 && i <= 41 && i != 36))
                {
                    if (reader.Byte() != 5) throw new InvalidDataException("Invalid nested AUX reset.");
                    if (i == 4) { equipment.Energy = 0; equipment.EnergyPercent = default; }
                    if (i == 6) { equipment.Shield = 0; equipment.ShieldPercent = default; }
                    continue;
                }
                if (!fields.Present[i] && fields.Default[i] && i == 42)
                {
                    if (reader.Byte() != 5) throw new InvalidDataException("Invalid buff reset.");
                    for (int slot = 0; slot < buffRevisions.Length; slot++)
                        buffRevisions[slot] = TimedDeviceBuff.Empty;
                    continue;
                }
                if (!fields.Present[i] && fields.Default[i] && (i == 44 || i == 45))
                {
                    if (reader.Byte() != 5) throw new InvalidDataException("Invalid ship stats reset.");
                    equipment.Defense = ShipDefenseState.Unknown;
                    continue;
                }
                if (!fields.Present[i] && fields.Default[i] && i == 36)
                {
                    if (reader.Byte() != 5) throw new InvalidDataException("Invalid inventory reset.");
                    slotsPresent = true;
                    Array.Clear(tradeSlots, 0, tradeSlots.Length);
                    continue;
                }
                if (!fields.Present[i] && fields.Default[i] && i == 43)
                { moneyPresent = true; tradeMoney = 0; continue; }
                if (!fields.Present[i] && fields.Default[i] && i >= 13 && i <= 17)
                {
                    if (i == 13) equipment.MaxImpulseSpeed = 0;
                    else if (i == 14) equipment.MinImpulseSpeed = 0;
                    else if (i == 15) equipment.ImpulseAcceleration = 0;
                    else if (i == 16) equipment.SpeedLocked = false;
                    else equipment.OrientationLocked = false;
                    continue;
                }
                if (!fields.Present[i]) continue;
                switch (i)
                {
                    case 0: case 1: case 2: case 3: case 28: case 29: reader.Text(); break;
                    case 4:
                        equipment.EnergyPercent = reader.TimedPercent(equipment.EnergyPercent);
                        equipment.Energy = equipment.EnergyPercent.Value(null,
                            System.Diagnostics.Stopwatch.GetTimestamp()); break;
                    case 5: equipment.MaxEnergy = reader.Float(); break;
                    case 6:
                        equipment.ShieldPercent = reader.TimedPercent(equipment.ShieldPercent);
                        equipment.Shield = equipment.ShieldPercent.Value(null,
                            System.Diagnostics.Stopwatch.GetTimestamp()); break;
                    case 7: equipment.MaxShield = reader.Float(); break;
                    case 8: equipment.Hull = reader.Float(); break;
                    case 9: equipment.MaxHull = reader.Float(); break;
                    case 10: case 11: case 12: reader.Float(); break;
                    case 13: equipment.MaxImpulseSpeed = reader.Float(); break;
                    case 14: equipment.MinImpulseSpeed = reader.Float(); break;
                    case 15: equipment.ImpulseAcceleration = reader.Float(); break;
                    case 16: equipment.SpeedLocked = reader.Byte() != 0; break;
                    case 17: equipment.OrientationLocked = reader.Byte() != 0; break;
                    case 18: case 19: case 20: case 21:
                    case 22: case 23: case 24: case 25: reader.Byte(); break;
                    case 27: equipment.TargetId = reader.UInt(); break;
                    case 26: case 30: case 33: case 34: reader.UInt(); break;
                    case 31: privateWarp = reader.UInt(); break;
                    case 32: globalWarp = reader.UInt(); break;
                    case 36: slotsPresent = reader.Inventory(equipment, tradeSlots); break;
                    case 35: case 37: reader.SkipNumericRecord(4); break;
                    case 42: reader.ReadBuff(buffRevisions); break;
                    case 43: moneyPresent = true;
                        tradeMoney = reader.UInt() | ((ulong)reader.UInt() << 32); break;
                    case 44: case 45: equipment.Defense = reader.ReadDefense(equipment.Defense); break;
                    default: return null;
                }
            }
            if (moneyPresent || slotsPresent)
                tradeRevision = new TradeOfferRevision(moneyPresent, tradeMoney, slotsPresent, tradeSlots);
            return privateWarp.HasValue || globalWarp.HasValue ?
                WarpState.Merge(privateWarp, globalWarp, previousWarp) : null;
        }

        /// <summary>
        /// Returns:
        ///     ShipDefenseState: A retained typed-deflect revision from nested ship stats.
        /// </summary>
        private ShipDefenseState ReadDefense(ShipDefenseState previous)
        {
            Flags stats = Mask(15);
            var deflects = new int[7];
            for (int channel = 0; channel < deflects.Length; channel++)
                deflects[channel] = previous.Deflects[channel];
            bool known = previous.Known;
            for (int field = 0; field < 15; field++)
            {
                if (stats.Present[field])
                {
                    int value = unchecked((int)UInt());
                    if (field >= 8) { deflects[field - 8] = value; known = true; }
                }
                else if (stats.Default[field])
                {
                    if (field >= 8) { deflects[field - 8] = 0; known = true; }
                }
            }
            return new ShipDefenseState(known, deflects);
        }

        /// <summary>Read all sixteen server-owned native buff slots.</summary>
        private void ReadBuff(TimedDeviceBuff[] revisions)
        {
            Flags slots = Mask(16);
            for (int slot = 0; slot < 16; slot++)
            {
                if (!slots.Present[slot] && slots.Default[slot])
                {
                    if (Byte() != 5) throw new InvalidDataException("Invalid buff slot reset.");
                    if (slot < revisions.Length) revisions[slot] = TimedDeviceBuff.Empty;
                    continue;
                }
                if (!slots.Present[slot]) continue;
                Flags fields = Mask(5);
                string name = "";
                uint removalTick = 0;
                bool permanent = false;
                for (int field = 0; field < 5; field++)
                {
                    if (!fields.Present[field]) continue;
                    switch (field)
                    {
                        case 0: name = Text(); break;
                        case 2: permanent = Byte() != 0; break;
                        case 3: removalTick = UInt(); break;
                        default: throw new InvalidDataException("Unsupported native buff field.");
                    }
                }
                if (slot < revisions.Length)
                {
                    TimedDeviceBuff revision = name.Length == 0 ||
                        (!permanent && removalTick == 0) ? TimedDeviceBuff.Empty :
                        new TimedDeviceBuff(name, removalTick, 0, 0, permanent);
                    revisions[slot] = revision;
                }
            }
        }
        /// <summary>Read ship inventory updates in native field order.</summary>
        /// Returns:
        ///     bool: Whether the six native trade slots were updated.
        private bool Inventory(EquipmentState equipment, ItemState[] tradeSlots)
        {
            Flags fields = Mask(11);
            bool tradeChanged = false;
            for (int i = 0; i < 11; i++)
            {
                if (!fields.Present[i] && fields.Default[i] && (i == 2 || i == 3 || i >= 6))
                {
                    if (Byte() != 5) throw new InvalidDataException("Invalid inventory reset.");
                    if (i == 2)
                    { Array.Clear(equipment.MountMasks, 0, equipment.MountMasks.Length); equipment.HasMountLayout = true; }
                    if (i == 10) { Array.Clear(tradeSlots, 0, tradeSlots.Length); tradeChanged = true; }
                    continue;
                }
                if (!fields.Present[i]) continue;
                switch (i)
                {
                    case 0:
                        uint capacity = UInt();
                        if (capacity == 0 || capacity > equipment.Cargo.Length)
                            throw new InvalidDataException("Invalid cargo capacity.");
                        equipment.CargoCapacity = capacity;
                        break;
                    case 1: Text(); break;
                    case 2: MountLayout(equipment); break;
                    case 3: SkipTextRecord(20); break;
                    case 4: equipment.WeaponMounts = UInt(); break;
                    case 5: equipment.DeviceMounts = UInt(); break;
                    case 6: ReadSlots(equipment.Cargo, 10); break;
                    case 7: ReadSlots(equipment.Mounted, 14); break;
                    case 8: ReadSlots(equipment.Ammo, 10); break;
                    case 9: SkipSlots(20, 10); break;
                    case 10: ReadSlots(tradeSlots, 10); tradeChanged = true; break;
                    default: return tradeChanged;
                }
            }
            return tradeChanged;
        }
        /// <summary>Retain native unlocked mount masks while preserving inventory packet alignment.</summary>
        private void MountLayout(EquipmentState equipment)
        {
            if (body[offset] == 5)
            { offset++; Array.Clear(equipment.MountMasks, 0, equipment.MountMasks.Length);
                equipment.HasMountLayout = true; return; }
            Flags fields = Mask(20);
            equipment.HasMountLayout = true;
            bool? legacyPadding = null;
            for (int i = 0; i < 20; i++)
            {
                if (fields.Default[i]) equipment.MountMasks[i] = 0;
                if (!fields.Present[i]) continue;
                equipment.MountMasks[i] = UInt();
                // Captured native snapshots append two reserved -1 words per
                // mount; compact fixtures can omit those reserved words.
                if (!legacyPadding.HasValue)
                    legacyPadding = offset + 8 <= body.Length &&
                        Wire.U32(body, offset) == uint.MaxValue && Wire.U32(body, offset + 4) == uint.MaxValue;
                if (legacyPadding.Value) { UInt(); UInt(); }
            }
        }
        /// <summary>Consume a native record of fixed-width numeric fields.</summary>
        private void SkipNumericRecord(int count)
        {
            Flags fields = Mask(count);
            for (int field = 0; field < count; field++) if (fields.Present[field]) UInt();
        }
        /// <summary>Consume a native record of length-prefixed strings.</summary>
        private void SkipTextRecord(int count)
        {
            Flags fields = Mask(count);
            for (int field = 0; field < count; field++) if (fields.Present[field]) Text();
        }
        /// <summary>Apply individual cargo or equipment slot updates.</summary>
        private void ReadSlots(ItemState[] slots, int fieldsPerItem)
        {
            Flags flags = Mask(slots.Length);
            for (int i = 0; i < slots.Length; i++)
            {
                if (flags.Present[i]) slots[i] = ReadItem(fieldsPerItem, slots[i]);
                else if (flags.Default[i]) { if (Byte() != 5) throw new InvalidDataException("Invalid item reset."); slots[i] = null; }
            }
        }
        /// <summary>Consume unused native slot collections.</summary>
        private void SkipSlots(int count, int fieldsPerItem)
        {
            Flags flags = Mask(count);
            for (int i = 0; i < count; i++)
            {
                if (flags.Present[i]) ReadItem(fieldsPerItem, null);
                else if (flags.Default[i] && Byte() != 5) throw new InvalidDataException("Invalid item reset.");
            }
        }
        /// <summary>Consume a typed native PlayerIndex field preceding a requested global field.</summary>
        private void SkipPlayerPrefixField(Flags global, int field)
        {
            if (global.Present[field])
            {
                switch (field)
                {
                    case 0: UInt(); UInt(); break; // Credits: u64.
                    case 1: case 8: UInt(); break; // XP debt and music ID.
                    case 2: SkipSlots(96, 10); break; // Secure inventory.
                    case 3: SkipSlots(128, 10); break; // Vendor inventory.
                    case 4: SkipSlots(2, 10); break; // Reward inventory.
                    case 5: SkipSlots(8, 10); break; // Overflow inventory.
                    case 6: SkipRpgForMissions(); break;
                    case 7: Text(); break; // Community event flags.
                    case 9: ReadMissionSlots(new MissionState[12]); break;
                    case 10: SkipReputation(); break;
                    case 11: case 15: case 16: UInt(); break;
                    case 12: case 13: case 14: Text(); break;
                    default: throw new InvalidDataException("Unsupported player AUX prefix field " + field + ".");
                }
            }
            else if (global.Default[field] && (field >= 2 && field <= 6 ||
                field == 9 || field == 10) && Byte() != 5)
                throw new InvalidDataException("Invalid player AUX prefix reset.");
        }
        /// <summary>Advance over native PlayerIndex fields in their fixed wire order.</summary>
        private void SkipPlayerPrefix(Flags global, int throughExclusive)
        {
            for (int field = 0; field < throughExclusive; field++)
                SkipPlayerPrefixField(global, field);
        }
        /// <returns>An updated item record, retaining unchanged weapon status fields.</returns>
        private ItemState ReadItem(int count, ItemState previous)
        {
            Flags flags = Mask(count); ItemState item = previous?.Copy() ?? new ItemState();
            for (int i = 0; i < count; i++)
            {
                if (!flags.Present[i] && flags.Default[i] && i == 13)
                { if (Byte() != 5) throw new InvalidDataException("Invalid equipment reset."); continue; }
                if (!flags.Present[i]) continue;
                switch (i)
                {
                    case 0: item.Template = UInt(); break;
                    case 1: item.Quantity = UInt(); break;
                    case 2: item.Price = UInt() | ((ulong)UInt() << 32); break;
                    case 3: Float(); break;
                    case 4: item.Structure = Float(); break;
                    case 5: item.Quality = Float(); break;
                    case 6: item.Info = Text(); break;
                    case 7: case 8: Text(); break;
                    case 9: item.BuilderName = Text(); break;
                    case 10: item.ReadyTick = UInt(); break;
                    case 11: item.Range = Float(); break;
                    case 12: item.WeaponState = UInt(); break;
                    case 13: SkipNumericRecord(4); break;
                    default: return item;
                }
            }
            return item;
        }

        /// Returns:
        ///     ItemState[]: Merged authoritative hulk cargo slots, or null when this diff does not update cargo.
        public static ItemState[] ReadCorpse(byte[] body, ItemState[] previous = null)
        {
            var reader = new AuxState(NativeCorpseEnvelope(body));
            ItemState[] contents = previous == null ? new ItemState[40] : (ItemState[])previous.Clone();
            if (contents.Length != 40) throw new InvalidDataException("Invalid previous corpse inventory size.");
            Wire.Require(reader.body, reader.offset, 1);
            if (reader.body[reader.offset] == 5)
            {
                reader.Byte();
                if (reader.offset != reader.body.Length) throw new InvalidDataException("Trailing corpse reset data.");
                return new ItemState[40];
            }
            Flags fields = reader.Mask(8);
            bool changed = false;
            for (int field = 0; field < 8; field++)
            {
                if (fields.Default[field] && field >= 2)
                {
                    if (reader.Byte() != 5) throw new InvalidDataException("Invalid native hulk field reset.");
                    if (field == 7) { contents = new ItemState[40]; changed = true; }
                    continue;
                }
                if (!fields.Present[field]) continue;
                switch (field)
                {
                    case 0: case 1: reader.Text(); break;
                    case 2: reader.SkipNumericRecord(4); break;
                    // Native AuxDamage's three records serialize no payload.
                    case 3: case 4: case 5: break;
                    case 6: reader.SkipSlots(20, 10); break;
                    case 7:
                        Wire.Require(reader.body, reader.offset, 1);
                        if (reader.body[reader.offset] == 5) { reader.Byte(); contents = new ItemState[40]; }
                        else reader.ReadSlots(contents, 10);
                        changed = true; break;
                }
            }
            if (reader.offset != reader.body.Length) throw new InvalidDataException("Unexpected native corpse inventory data.");
            return changed ? contents : null;
        }

        /// <returns>The server-owned credit balance when this global AUX updates it.</returns>
        public static ulong? ReadCredits(byte[] body)
        {
            var reader = new AuxState(body); Flags fields = reader.Mask(18);
            if (!fields.Present[0]) return null;
            return reader.UInt() | ((ulong)reader.UInt() << 32);
        }

        /// <summary>Apply a global AUX vault snapshot or slot/price revision.</summary>
        public static void ReadVault(byte[] body, EquipmentState equipment)
        {
            var reader = new AuxState(body); Flags fields = reader.Mask(18);
            reader.SkipPlayerPrefix(fields, 2);
            if (fields.Present[2]) reader.ReadSlots(equipment.Vault, 10);
            else if (fields.Default[2])
            {
                if (reader.Byte() != 5) throw new InvalidDataException("Invalid vault reset.");
                equipment.Vault = new ItemState[96];
            }
        }

        /// <returns>uint[]: Ability IDs advertised for this character, or null for another AUX update.</returns>
        public static uint[] ReadAvailableAbilities(byte[] body)
        {
            var reader = new AuxState(body); Flags outer = reader.Mask(18);
            if (!outer.Present[6]) return null;
            reader.SkipPlayerPrefix(outer, 6);
            Flags rpg = reader.Mask(16);
            if (!rpg.Present[3]) return null;
            if (rpg.Present[0]) reader.UInt();
            if (rpg.Present[1]) reader.UInt();
            if (rpg.Present[2]) reader.SkipSkillSlots();
            else if (rpg.Default[2] && reader.Byte() != 5)
                throw new InvalidDataException("Invalid skill reset.");
            Flags slots = reader.Mask(170);
            var available = new List<uint>();
            for (uint id = 0; id < 170; id++)
            {
                if (slots.Present[id])
                {
                    Flags ability = reader.Mask(7);
                    for (int field = 0; field < 7; field++)
                    {
                        if (!ability.Present[field]) continue;
                        if (field == 0) { if (reader.Byte() != 0) available.Add(id); }
                        else if (field == 6) reader.Byte();
                        else reader.UInt();
                    }
                }
                else if (slots.Default[id] && reader.Byte() != 5)
                    throw new InvalidDataException("Invalid ability reset.");
            }
            return available.ToArray();
        }

        /// <summary>Consume the server's skill ranks before its ability list.</summary>
        private void SkipSkillSlots()
        {
            Flags slots = Mask(64);
            for (int skill = 0; skill < 64; skill++)
            {
                if (slots.Present[skill])
                {
                    Flags fields = Mask(7);
                    for (int field = 0; field < 7; field++)
                    {
                        if (!fields.Present[field]) continue;
                        if (field == 3) { UInt(); UInt(); UInt(); UInt(); }
                        else if (field == 6) Text();
                        else UInt();
                    }
                }
                else if (slots.Default[skill] && Byte() != 5)
                    throw new InvalidDataException("Invalid skill slot reset.");
            }
        }

        /// <summary>Consume native ability availability records before later RPG fields.</summary>
        private void SkipAbilitySlots()
        {
            Flags slots = Mask(170);
            for (int slot = 0; slot < 170; slot++)
            {
                if (slots.Present[slot])
                {
                    Flags entry = Mask(7);
                    for (int field = 0; field < 7; field++)
                    {
                        if (!entry.Present[field]) continue;
                        if (field == 0 || field == 6) Byte();
                        else UInt();
                    }
                }
                else if (slots.Default[slot] && Byte() != 5)
                    throw new InvalidDataException("Invalid ability reset before RPG progress.");
            }
        }

        /// <returns>AbilityProgress: Native cast progress, or null for another AUX update.</returns>
        public static AbilityProgress ReadAbilityProgress(byte[] body)
        {
            var reader = new AuxState(body); Flags outer = reader.Mask(18);
            if (!outer.Present[6]) return null;
            for (int i = 0; i < 6; i++) if (outer.Present[i]) return null;
            Flags rpg = reader.Mask(16);
            if (!rpg.Present[13] || !rpg.Present[14] || !rpg.Present[15]) return null;
            for (int i = 0; i < 13; i++) if (rpg.Present[i]) return null;
            uint start = reader.UInt(), finish = reader.UInt(), id = reader.UInt();
            uint duration = unchecked(finish - start);
            if (duration > 120000) throw new InvalidDataException("Invalid ability charge duration.");
            return new AbilityProgress(id, duration);
        }

        /// <returns>SkillCooldown[]: Native recovery updates, or null for another AUX update.</returns>
        public static SkillCooldown[] ReadSkillCooldowns(byte[] body)
        {
            var reader = new AuxState(body); Flags outer = reader.Mask(18);
            if (!outer.Present[6]) return null;
            for (int i = 0; i < 6; i++) if (outer.Present[i]) return null;
            Flags rpg = reader.Mask(16);
            if (!rpg.Present[2]) return null;
            if (rpg.Present[0] || rpg.Present[1]) return null;
            Flags slots = reader.Mask(64);
            var cooldowns = new List<SkillCooldown>();
            for (uint skill = 0; skill < 64; skill++)
            {
                if (slots.Default[skill])
                {
                    if (reader.Byte() != 5) throw new InvalidDataException("Invalid cooldown skill reset.");
                    cooldowns.Add(new SkillCooldown(skill, 0)); continue;
                }
                if (!slots.Present[skill]) continue;
                if (reader.ReadSkillReset()) { cooldowns.Add(new SkillCooldown(skill, 0)); continue; }
                Flags fields = reader.Mask(7);
                uint duration = 0;
                bool changed = fields.Present[1] || fields.Default[1];
                for (int field = 0; field < 7; field++)
                {
                    if (!fields.Present[field]) continue;
                    switch (field)
                    {
                        case 1: duration = reader.UInt(); break;
                        case 3: reader.UInt(); reader.UInt(); reader.UInt(); reader.UInt(); break;
                        case 6: reader.Text(); break;
                        default: reader.UInt(); break;
                    }
                }
                if (duration > 120000) throw new InvalidDataException("Invalid skill cooldown.");
                if (changed) cooldowns.Add(new SkillCooldown(skill, duration));
            }
            return cooldowns.ToArray();
        }

        /// <returns>A complete vendor stock update, or null for another global AUX.</returns>
        public static ItemState[] ReadVendor(byte[] body)
        {
            var reader = new AuxState(body); Flags fields = reader.Mask(18);
            if (!fields.Present[3]) return null;
            reader.SkipPlayerPrefix(fields, 3);
            var stock = new ItemState[128];
            reader.ReadSlots(stock, 10);
            return stock;
        }

        /// <returns>The resource contents and level after one native AUX revision.</returns>
        public static ResourceState ReadResource(byte[] body, uint id, ResourceState previous)
        {
            var reader = new AuxState(body); Flags fields = reader.Mask(4);
            string name = previous?.Name ?? "";
            uint level = previous?.Level ?? 0;
            float fraction = previous?.Fraction ?? 1;
            ItemState[] items = previous == null ? new ItemState[40] : (ItemState[])previous.Items.Clone();
            if (fields.Present[0]) name = reader.Text();
            if (fields.Present[1]) reader.ReadSlots(items, 10);
            else if (fields.Default[1])
            {
                if (reader.Byte() != 5) throw new InvalidDataException("Invalid resource inventory reset.");
                items = new ItemState[40];
            }
            if (fields.Present[2]) fraction = reader.Float();
            if (fields.Present[3]) level = reader.UInt();
            if (fraction < 0 || fraction > 1 || level > 10)
                throw new InvalidDataException("Invalid resource level or depletion.");
            return new ResourceState(id, name, level, fraction, items);
        }

        /// <returns>The server's standalone group update, or null for unrelated player AUX.</returns>
        public static GroupState ReadGroup(byte[] body)
        {
            var reader = new AuxState(body); Flags outer = reader.Mask(18);
            if (!outer.Present[17]) return null;
            reader.SkipPlayerPrefix(outer, 17);
            Flags info = reader.Mask(11); bool leader = false, formed = false; string formation = "";
            var members = new List<GroupMember>();
            for (int i = 0; i < 10; i++)
            {
                if (!info.Present[i]) continue;
                if (i <= 6) { byte value = reader.Byte(); if (i == 0) leader = value != 0; }
                else if (i == 7) formation = reader.Text();
                else { uint value = reader.UInt(); if (i == 8) formed = value != 0; }
            }
            if (info.Present[10])
            {
                Flags slots = reader.Mask(5);
                for (int i = 0; i < 5; i++)
                {
                    if (!slots.Present[i] && !slots.Default[i]) continue;
                    Flags member = reader.Mask(4); string name = ""; uint id = 0, memberFormation = 0;
                    if (member.Present[0]) name = reader.Text();
                    if (member.Present[1]) id = reader.UInt();
                    if (member.Present[2]) memberFormation = reader.UInt();
                    uint memberSlot = member.Present[3] ? reader.UInt() : uint.MaxValue;
                    if (id != 0) members.Add(new GroupMember(id, name, memberFormation != 0, memberSlot));
                }
            }
            return new GroupState(leader, members.ToArray(), formation, formed);
        }
    }
}
