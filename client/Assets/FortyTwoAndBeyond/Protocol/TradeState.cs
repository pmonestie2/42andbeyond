using System;
using System.Collections.Generic;
using System.IO;

namespace FortyTwoAndBeyond.Protocol
{
    /// <summary>One partial trade-offer revision carried by a native ship AUX diff.</summary>
    internal sealed class TradeOfferRevision
    {
        public readonly bool HasMoney, HasSlots;
        public readonly ulong Money;
        public readonly ItemState[] Slots;

        /// <summary>Keep absent AUX fields distinct from explicit zero or empty offers.</summary>
        public TradeOfferRevision(bool hasMoney, ulong money, bool hasSlots, ItemState[] slots)
        { HasMoney = hasMoney; Money = money; HasSlots = hasSlots; Slots = slots; }
    }

    /// <summary>An authoritative six-slot offer retained on the network worker.</summary>
    internal sealed class TradeOfferState
    {
        public readonly ulong Money;
        public readonly ItemState[] Slots;

        /// <summary>Copy every item so a later AUX diff cannot change an earlier offer.</summary>
        public TradeOfferState(ulong money, ItemState[] slots)
        {
            Money = money;
            Slots = new ItemState[6];
            if (slots == null) return;
            if (slots.Length != Slots.Length) throw new ArgumentException("Trade offers need six slots.", nameof(slots));
            for (int slot = 0; slot < Slots.Length; slot++) Slots[slot] = slots[slot]?.Copy();
        }

        /// <summary>
        /// Returns:
        ///     TradeOfferState: A new offer incorporating only server-present fields.
        /// </summary>
        public TradeOfferState WithRevision(TradeOfferRevision revision)
        { return new TradeOfferState(revision.HasMoney ? revision.Money : Money,
            revision.HasSlots ? revision.Slots : Slots); }
    }

    /// <summary>One server-controlled peer trade session and its confirmation flags.</summary>
    internal sealed class TradeState
    {
        public static readonly TradeState Closed = new TradeState(0, false, false,
            new TradeOfferState(0, null), new TradeOfferState(0, null));
        public readonly uint PeerId;
        public readonly bool OwnConfirmed, PeerConfirmed;
        public readonly TradeOfferState Own, Peer;
        public bool Open => PeerId != 0;

        private TradeState(uint peerId, bool ownConfirmed, bool peerConfirmed,
            TradeOfferState own, TradeOfferState peer)
        { PeerId = peerId; OwnConfirmed = ownConfirmed; PeerConfirmed = peerConfirmed;
            Own = own; Peer = peer; }

        /// <summary>
        /// Returns:
        ///     TradeState: A new trade revision from the native 0x1f lifecycle message.
        /// Raises:
        ///     InvalidDataException: The peer or action is malformed.
        /// </summary>
        public TradeState ApplyControl(byte[] body)
        {
            if (body.Length != 5 || body[4] > 6)
                throw new InvalidDataException("Invalid native trade control.");
            uint peerId = Wire.U32(body, 0);
            byte action = body[4];
            if (action == 0)
            {
                if (peerId == 0) throw new InvalidDataException("Trade open has no peer.");
                return new TradeState(peerId, false, false,
                    new TradeOfferState(0, null), new TradeOfferState(0, null));
            }
            if (peerId == 0 && action <= 2)
                throw new InvalidDataException("Trade close has no peer.");
            if (!Open || (peerId != 0 && peerId != PeerId)) return this;
            if (action == 1 || action == 2) return Closed;
            if (action == 3) return new TradeState(PeerId, true, PeerConfirmed, Own, Peer);
            if (action == 5) return new TradeState(PeerId, OwnConfirmed, true, Own, Peer);
            if (action == 4) return new TradeState(PeerId, false, false, Own, Peer);
            return new TradeState(PeerId, OwnConfirmed, false, Own, Peer);
        }

        /// <summary>
        /// Returns:
        ///     TradeState: The offer update for this trade's own or partner identity.
        /// </summary>
        public TradeState WithOffer(uint ownerId, uint playerId, TradeOfferRevision revision)
        {
            if (!Open || revision == null) return this;
            if (ownerId == playerId) return new TradeState(PeerId, OwnConfirmed,
                PeerConfirmed, Own.WithRevision(revision), Peer);
            if (ownerId == PeerId) return new TradeState(PeerId, OwnConfirmed,
                PeerConfirmed, Own, Peer.WithRevision(revision));
            return this;
        }
    }

    /// <summary>One immutable offer copied for Unity presentation.</summary>
    public sealed class TradeOfferView
    {
        public readonly ulong Money;
        public readonly IReadOnlyList<ItemView> Items;

        /// <summary>Resolve all six server-owned slots without exposing worker item records.</summary>
        internal TradeOfferView(TradeOfferState offer, IReadOnlyDictionary<uint, ItemTemplateInfo> catalog)
        {
            Money = offer.Money;
            var items = new ItemView[offer.Slots.Length];
            for (int slot = 0; slot < items.Length; slot++)
                if (offer.Slots[slot]?.Present == true)
                    items[slot] = new ItemView(slot, offer.Slots[slot], catalog);
            Items = Array.AsReadOnly(items);
        }
    }

    /// <summary>Immutable display snapshot of a native peer trade.</summary>
    public sealed class TradeView
    {
        public static readonly TradeView Closed = new TradeView();
        public readonly uint PeerId;
        public readonly string PeerName;
        public readonly bool OwnConfirmed, PeerConfirmed;
        public readonly TradeOfferView Own, Peer;
        public bool Open => PeerId != 0;

        private TradeView() { PeerName = ""; }

        /// <summary>Copy names, flags, credits and items into one stable main-thread view.</summary>
        internal TradeView(TradeState state, string peerName,
            IReadOnlyDictionary<uint, ItemTemplateInfo> catalog)
        {
            PeerId = state.PeerId; PeerName = peerName ?? "Pilot " + state.PeerId;
            OwnConfirmed = state.OwnConfirmed; PeerConfirmed = state.PeerConfirmed;
            Own = new TradeOfferView(state.Own, catalog);
            Peer = new TradeOfferView(state.Peer, catalog);
        }
    }
}
