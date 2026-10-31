using System;
using System.IO;
using FortyTwoAndBeyond.Protocol;

internal static class TradeChecks
{
    /// <summary>Exercise native trade lifecycle, offer revisions, and immutable snapshots.</summary>
    public static void Check()
    {
        const uint self = 0x40000001, peer = 0x40000002;
        var world = new World();
        world.Apply(new Frame(0x1f, Control(peer, 0)), self);
        Snapshot opened = new Snapshot(world, "Ready", true);
        Require(opened.Trade.Open && opened.Trade.PeerId == peer,
            "Native 0x1f did not publish an open immutable trade snapshot.");
        world.Apply(new Frame(0x1b, Fixture("trade_offer_own.hex")), self);
        world.Apply(new Frame(0x1b, Fixture("trade_offer_peer.hex")), self);
        Snapshot offers = new Snapshot(world, "Ready", true);
        Require(offers.Trade.Own.Money == 1234567890123UL &&
            offers.Trade.Own.Items[2]?.Template == 2505 &&
            offers.Trade.Own.Items[2]?.Quantity == 1 &&
            offers.Trade.Own.Items[2]?.BuilderName == "Trader" &&
            offers.Trade.Peer.Money == 4321 && offers.Trade.Peer.Items[2] == null,
            "Server-encoded trade AUX lost own or peer offers.");
        var state = TradeState.Closed;
        state = state.ApplyControl(Control(peer, 0));
        Require(state.Open && state.PeerId == peer && !state.OwnConfirmed, "Trade did not open.");
        var offered = new ItemState[6];
        offered[2] = new ItemState { Template = 2505, Quantity = 3 };
        state = state.WithOffer(self, self, new TradeOfferRevision(true, 1234, true, offered));
        offered[2].Quantity = 9;
        Require(state.Own.Money == 1234 && state.Own.Slots[2].Quantity == 3,
            "Offer aliased its source item.");
        var beforeConfirm = new TradeView(state, "Partner",
            new System.Collections.Generic.Dictionary<uint, ItemTemplateInfo>());
        state = state.ApplyControl(Fixture("trade_confirm_self.hex")).ApplyControl(Fixture("trade_confirm_peer.hex"));
        Require(state.OwnConfirmed && state.PeerConfirmed,
            "Native peer-zero approval flags were lost.");
        world.Apply(new Frame(0x1f, Fixture("trade_confirm_self.hex")), self);
        world.Apply(new Frame(0x1f, Fixture("trade_confirm_peer.hex")), self);
        Require(new Snapshot(world, "Ready", true).Trade.OwnConfirmed &&
            new Snapshot(world, "Ready", true).Trade.PeerConfirmed,
            "Peer-zero native confirmations were not published.");
        state = state.ApplyControl(Fixture("trade_reset_both.hex"));
        Require(!state.OwnConfirmed && !state.PeerConfirmed && state.Own.Slots[2].Quantity == 3,
            "Offer edit did not reset both approvals without erasing offers.");
        state = state.WithOffer(peer, self, new TradeOfferRevision(true, 88, false, null));
        Require(state.Peer.Money == 88 && state.Own.Money == 1234,
            "Partner credit update changed the own offer.");
        Require(beforeConfirm.Own.Items[2].Quantity == 3 && beforeConfirm.Own.Money == 1234,
            "A published trade view changed after a worker update.");
        state = state.ApplyControl(Fixture("trade_confirm_peer.hex")).ApplyControl(Fixture("trade_reset_peer.hex"));
        Require(!state.PeerConfirmed, "Peer-only approval reset was lost.");
        Require(!TradeState.Closed.ApplyControl(Fixture("trade_confirm_self.hex")).Open &&
            !TradeState.Closed.ApplyControl(Fixture("trade_confirm_peer.hex")).Open,
            "A peer-zero confirmation reopened a closed trade.");
        try { TradeState.Closed.ApplyControl(Control(0, 0));
            throw new Exception("A peer-zero open was accepted."); }
        catch (InvalidDataException) { }
        Require(state.ApplyControl(Control(peer + 1, 1)).Open,
            "An unrelated peer closed the active trade.");
        Require(!state.ApplyControl(Control(peer, 1)).Open, "Cancel did not close trade.");
        world.Apply(new Frame(0x1f, Control(peer, 1)), self);
        Require(!new Snapshot(world, "Ready", true).Trade.Open && opened.Trade.Open &&
            offers.Trade.Own.Items[2].Template == 2505,
            "Native cancellation altered an older published trade snapshot.");
        Require(!state.ApplyControl(Control(peer, 2)).Open, "Completion did not close trade.");
        try { state.ApplyControl(new byte[4]); throw new Exception("Short control was accepted."); }
        catch (InvalidDataException) { }
        Console.WriteLine("PASS: trade lifecycle, offer edits, approval resets, cancellation and immutable views");
    }

    /// <returns>
    ///     byte[]: A server-encoded native trade offer body.
    /// </returns>
    private static byte[] Fixture(string filename)
    { return Convert.FromHexString(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
        "Fixtures", filename)).Trim()); }

    /// <returns>
    ///     byte[]: One native peer-trade control body.
    /// </returns>
    private static byte[] Control(uint peer, byte action)
    { return new[] { (byte)peer, (byte)(peer >> 8), (byte)(peer >> 16),
        (byte)(peer >> 24), action }; }

    /// <summary>Fail the pure check when a native trade invariant is broken.</summary>
    private static void Require(bool condition, string message)
    { if (!condition) throw new Exception(message); }
}
