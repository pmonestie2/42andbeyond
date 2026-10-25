//! Shared command dispatch, chat, loot permissions and group-scoped skills.
//! Membership is checked here at
//! delivery time as well as at cast time: a stale client selection cannot buff,
//! teleport or revive somebody who left the group during the cast.
use super::*;
use crate::mob::distance;

impl GameState {
    /// Settle one selected Reactor Optimization status in the atomic world ledger.
    pub(super) fn deliver_reactor_optimizations(&mut self) -> io::Result<()> {
        let sources: Vec<u32> = self
            .sessions
            .iter()
            .filter_map(|(id, session)| session.pending_reactor_optimization().map(|_| *id))
            .collect();
        for source in sources {
            let pending = self.sessions[&source]
                .pending_reactor_optimization()
                .unwrap();
            if !pending.valid() {
                return Err(invalid("Invalid saved Reactor Optimization cast."));
            }
            self.sessions
                .get_mut(&source)
                .unwrap()
                .checkpoint_session(true)?;
            let epoch = self.sessions[&source].energy_transfer_epoch();
            if !self
                .energy_ledger
                .reactor_optimization_seen(source, epoch, pending.effect)
            {
                self.update_views();
                let caster = &self.sessions[&source];
                let recipient = self.sessions.get(&pending.target);
                let tier = crate::reactor_optimization::tier(pending.ability, pending.trained_rank)
                    .ok_or_else(|| invalid("Unknown Reactor Optimization tier."))?;
                let eligible = recipient.is_some_and(|recipient| {
                    caster.reactor_optimization_available()
                        && recipient.reactor_optimization_available()
                        && recipient.sector.id == caster.sector.id
                        && caster.reactor_optimization_contact(pending.target)
                        && (pending.target == source
                            || tier.rank >= 5
                                && distance(recipient.position, caster.position) <= tier.range)
                });
                if !eligible {
                    self.sessions
                        .get_mut(&source)
                        .unwrap()
                        .clear_pending_reactor_optimization()?;
                    continue;
                }
                let recipient = recipient.unwrap();
                self.energy_ledger.cast_reactor_optimization(
                    source,
                    epoch,
                    pending.effect,
                    caster.energy_balance().0,
                    pending.target,
                    crate::reactor_optimization::ReactorOptimizationBuff {
                        revision: 0,
                        source,
                        target_epoch: recipient.energy_transfer_epoch(),
                        ability: pending.ability,
                        trained_rank: pending.trained_rank,
                        expires_at_ms: crate::resources::unix_ms()
                            .saturating_add(crate::reactor_optimization::DURATION_MS),
                    },
                )?;
            }
            if let Some(balance) = self.energy_ledger.balance(source) {
                if let Err(error) = self
                    .sessions
                    .get_mut(&source)
                    .unwrap()
                    .apply_energy_balance(balance)
                {
                    crate::server_error!(
                        "Reactor Optimization sender checkpoint retry for {source}: {error}"
                    );
                }
            }
            if let Some(buff) = self.energy_ledger.reactor_optimization_buff(pending.target) {
                if buff.source == source {
                    if let Some(recipient) = self.sessions.get_mut(&pending.target) {
                        if let Err(error) = recipient.apply_reactor_optimization_buff(buff.clone())
                        {
                            crate::server_error!(
                                "Reactor Optimization recipient checkpoint retry for {}: {error}",
                                pending.target
                            );
                        }
                    }
                }
            }
            self.sessions
                .get_mut(&source)
                .unwrap()
                .clear_pending_reactor_optimization()?;
        }
        Ok(())
    }

    /// Settle one selected Shield Charging status in the atomic world ledger.
    pub(super) fn deliver_shield_chargings(&mut self) -> io::Result<()> {
        let sources: Vec<u32> = self
            .sessions
            .iter()
            .filter_map(|(id, session)| session.pending_shield_charging().map(|_| *id))
            .collect();
        for source in sources {
            let pending = self.sessions[&source].pending_shield_charging().unwrap();
            if !pending.valid() {
                return Err(invalid("Invalid saved Shield Charging cast."));
            }
            self.sessions
                .get_mut(&source)
                .unwrap()
                .checkpoint_session(true)?;
            let epoch = self.sessions[&source].energy_transfer_epoch();
            if !self
                .energy_ledger
                .shield_charging_seen(source, epoch, pending.effect)
            {
                self.update_views();
                let caster = &self.sessions[&source];
                let recipient = self.sessions.get(&pending.target);
                let tier = crate::shield_charging::tier(pending.ability, pending.trained_rank)
                    .ok_or_else(|| invalid("Unknown Shield Charging tier."))?;
                let eligible = recipient.is_some_and(|recipient| {
                    caster.shield_charging_available()
                        && recipient.shield_charging_available()
                        && recipient.sector.id == caster.sector.id
                        && caster.shield_charging_contact(pending.target)
                        && (pending.target == source
                            || tier.rank >= 5
                                && distance(recipient.position, caster.position) <= tier.range)
                });
                if !eligible {
                    self.sessions
                        .get_mut(&source)
                        .unwrap()
                        .clear_pending_shield_charging()?;
                    continue;
                }
                let recipient = recipient.unwrap();
                self.energy_ledger.cast_shield_charging(
                    source,
                    epoch,
                    pending.effect,
                    caster.energy_balance().0,
                    pending.target,
                    crate::shield_charging::ShieldChargingBuff {
                        revision: 0,
                        source,
                        target_epoch: recipient.energy_transfer_epoch(),
                        ability: pending.ability,
                        trained_rank: pending.trained_rank,
                        expires_at_ms: crate::resources::unix_ms()
                            .saturating_add(crate::shield_charging::DURATION_MS),
                    },
                )?;
            }
            if let Some(balance) = self.energy_ledger.balance(source) {
                if let Err(error) = self
                    .sessions
                    .get_mut(&source)
                    .unwrap()
                    .apply_energy_balance(balance)
                {
                    crate::server_error!(
                        "Shield Charging sender checkpoint retry for {source}: {error}"
                    );
                }
            }
            if let Some(buff) = self.energy_ledger.shield_charging_buff(pending.target) {
                if buff.source == source {
                    if let Some(recipient) = self.sessions.get_mut(&pending.target) {
                        if let Err(error) = recipient.apply_shield_charging_buff(buff.clone()) {
                            crate::server_error!(
                                "Shield Charging recipient checkpoint retry for {}: {error}",
                                pending.target
                            );
                        }
                    }
                }
            }
            self.sessions
                .get_mut(&source)
                .unwrap()
                .clear_pending_shield_charging()?;
        }
        Ok(())
    }

    /// Settle one Rally cast for all nearby grouped pilots in one world revision.
    pub(super) fn deliver_rallies(&mut self) -> io::Result<()> {
        let sources: Vec<u32> = self
            .sessions
            .iter()
            .filter_map(|(id, session)| session.pending_rally().map(|_| *id))
            .collect();
        for source in sources {
            let pending = self.sessions[&source].pending_rally().unwrap();
            if !pending.valid() {
                return Err(invalid("Invalid saved Rally cast."));
            }
            self.sessions
                .get_mut(&source)
                .unwrap()
                .checkpoint_session(true)?;
            let epoch = self.sessions[&source].energy_transfer_epoch();
            if !self.energy_ledger.rally_seen(source, epoch, pending.effect) {
                self.update_views();
                let caster = &self.sessions[&source];
                if !caster.rally_available() {
                    self.sessions
                        .get_mut(&source)
                        .unwrap()
                        .clear_pending_rally()?;
                    continue;
                }
                let recipients = std::iter::once(source)
                    .chain(
                        self.groups
                            .members(source)
                            .into_iter()
                            .filter(|id| *id != source),
                    )
                    .filter_map(|target| {
                        let recipient = self.sessions.get(&target)?;
                        (recipient.rally_available()
                            && recipient.sector.id == caster.sector.id
                            && distance(recipient.position, caster.position)
                                <= crate::rally::range(pending.skill_rank))
                        .then_some((
                            target,
                            crate::rally::RallyBuff {
                                revision: 0,
                                source,
                                target_epoch: recipient.energy_transfer_epoch(),
                                tactic: pending.tactic,
                                skill_rank: pending.skill_rank,
                                expires_at_ms: crate::resources::unix_ms()
                                    .saturating_add(crate::rally::DURATION_MS),
                            },
                        ))
                    })
                    .collect();
                self.energy_ledger.cast_rally(
                    source,
                    epoch,
                    pending.effect,
                    caster.energy_balance().0,
                    recipients,
                )?;
            }
            if let Some(balance) = self.energy_ledger.balance(source) {
                if let Err(error) = self
                    .sessions
                    .get_mut(&source)
                    .unwrap()
                    .apply_energy_balance(balance)
                {
                    crate::server_error!("Rally sender checkpoint retry for {source}: {error}");
                }
            }
            let targets: Vec<(u32, crate::rally::RallyBuff)> = self
                .sessions
                .keys()
                .filter_map(|target| {
                    self.energy_ledger
                        .rally_buff(*target)
                        .filter(|buff| buff.source == source)
                        .cloned()
                        .map(|buff| (*target, buff))
                })
                .collect();
            for (target, buff) in targets {
                if let Err(error) = self
                    .sessions
                    .get_mut(&target)
                    .unwrap()
                    .apply_rally_buff(buff)
                {
                    crate::server_error!("Rally recipient checkpoint retry for {target}: {error}");
                }
            }
            self.sessions
                .get_mut(&source)
                .unwrap()
                .clear_pending_rally()?;
        }
        Ok(())
    }

    pub(super) fn handle_event(&mut self, source: u32, event: ClientEvent) -> io::Result<()> {
        if !self
            .sessions
            .get(&source)
            .is_some_and(|session| session.logged_in)
        {
            return Err(invalid("Enter the game before using social commands."));
        }
        match event {
            ClientEvent::LootOpen { target, toggle } => {
                self.update_views();
                self.sessions
                    .get_mut(&source)
                    .unwrap()
                    .request_loot_open(target, toggle)
            }
            ClientEvent::LootBegin { target, slot } => {
                self.update_views();
                self.sessions
                    .get_mut(&source)
                    .unwrap()
                    .request_loot_begin(target, slot)
            }
            ClientEvent::Group { action, target } => self.group_action(source, action, target),
            ClientEvent::JumpstartOffer { target } => self.jumpstart_offer(source, target),
            ClientEvent::EnergyTransfer {
                target,
                slot,
                epoch,
                sequence,
            } => self.transfer_reactor_energy(source, target, slot, epoch, sequence),
            ClientEvent::PeerDeviceBuff {
                target,
                slot,
                epoch,
                sequence,
            } => self.cast_peer_device_buff(source, target, slot, epoch, sequence),
            ClientEvent::Chat { channel, text } => self.chat(source, channel, &text),
            ClientEvent::Formation { action, target } => {
                match action {
                    4..=6 => {
                        if !self.sessions[&source].can_form_up() {
                            return Err(invalid(
                                "Choose a formation alive in space, outside warp.",
                            ));
                        }
                        self.groups.set_formation(source, action)?;
                    }
                    7 => {
                        if !self.sessions[&source].can_form_up() {
                            return Err(invalid("Leave warp and recover before forming up."));
                        }
                        let group = self
                            .groups
                            .group(source)
                            .ok_or_else(|| invalid("You are not in a group."))?;
                        let follower = self.sessions[&source]
                            .peer_snapshot()
                            .ok_or_else(|| invalid("Enter space before forming up."))?;
                        let leader = self.sessions[&group.leader]
                            .peer_snapshot()
                            .ok_or_else(|| invalid("The leader is not in space."))?;
                        if follower.hull <= 0.
                            || leader.hull <= 0.
                            || follower.sector != leader.sector
                            || distance(follower.position, leader.position) > 5000.
                        {
                            return Err(invalid(
                                "Form up alive within 5000m of your leader in the same sector.",
                            ));
                        }
                        self.groups.form_up(source)?;
                    }
                    8 => self.groups.leave_formation(source),
                    9 => self.groups.set_formation(source, 0)?,
                    12 => {
                        let selected = self.sessions[&source].current_target();
                        for id in self.groups.members(source) {
                            if id != source && (target == 0 || target == id) {
                                self.sessions.get_mut(&id).unwrap().group_target(selected)?;
                            }
                        }
                    }
                    _ => return Err(invalid("This formation command is not implemented yet.")),
                }
                let mut body = Vec::from(source.to_le_bytes());
                body.extend_from_slice(&15u32.to_le_bytes());
                body.push(1);
                self.sessions
                    .get_mut(&source)
                    .unwrap()
                    .queue([Frame::new(0xbd, body)]);
                self.groups_dirty = true;
                Ok(())
            }
        }
    }

    /// Settle a target-only reactor transfer through one atomic world revision.
    fn transfer_reactor_energy(
        &mut self,
        source: u32,
        target: u32,
        slot: u8,
        epoch: u64,
        sequence: u32,
    ) -> io::Result<()> {
        if sequence == 0 || epoch != self.sessions[&source].energy_transfer_epoch() {
            return Err(invalid("Stale reactor transfer request."));
        }
        if self.energy_ledger.seen(source, epoch, sequence) {
            self.sessions
                .get_mut(&source)
                .unwrap()
                .message("Transfer Reactor Energy completed.");
            return Ok(());
        }
        self.update_views();
        let sender = &self.sessions[&source];
        let offer = sender.energy_transfer_offer(target, slot)?;
        let recipient = self
            .sessions
            .get(&target)
            .ok_or_else(|| invalid("Target pilot is unavailable."))?;
        let recipient_snapshot = recipient
            .peer_snapshot()
            .ok_or_else(|| invalid("Target pilot is unavailable."))?;
        if !recipient.logged_in
            || recipient_snapshot.hull <= 0.
            || recipient_snapshot.sector != sender.sector.id
            || distance(sender.position, recipient.position) > offer.range
        {
            return Err(invalid("Target pilot left reactor transfer range."));
        }
        let (source_energy, _) = sender.energy_balance();
        let (target_energy, target_capacity) = recipient.energy_balance();
        if target_energy >= target_capacity {
            return Err(invalid("Target reactor is already full."));
        }
        let source_after = source_energy - offer.cost;
        let target_after = (target_energy + offer.amount).min(target_capacity);
        self.energy_ledger
            .transfer(source, target, epoch, sequence, source_after, target_after)?;
        // The ledger is now authoritative. A checkpoint write failure must
        // not undo one side: replacement reapplies the same revision to both.
        for id in [source, target] {
            let balance = self.energy_ledger.balance(id).unwrap();
            if let Err(error) = self
                .sessions
                .get_mut(&id)
                .unwrap()
                .apply_energy_balance(balance)
            {
                crate::server_error!("Energy transfer checkpoint retry for {id}: {error}");
            }
        }
        self.sessions
            .get_mut(&source)
            .unwrap()
            .message("Transfer Reactor Energy completed.");
        self.sessions
            .get_mut(&target)
            .unwrap()
            .message("Reactor energy received from a nearby pilot.");
        Ok(())
    }

    /// Commit sender reactor cost and a friendly buff in one world file.
    fn cast_peer_device_buff(
        &mut self,
        source: u32,
        target: u32,
        slot: u8,
        epoch: u64,
        sequence: u32,
    ) -> io::Result<()> {
        if sequence == 0 || epoch != self.sessions[&source].energy_transfer_epoch() {
            return Err(invalid("Stale friendly device activation."));
        }
        if self.energy_ledger.seen(source, epoch, sequence) {
            self.sessions
                .get_mut(&source)
                .unwrap()
                .message("Friendly device effect sent.");
            return Ok(());
        }
        self.update_views();
        if !self.groups.together(source, target) {
            return Err(invalid("This device requires a grouped friendly pilot."));
        }
        let sender = &self.sessions[&source];
        let offer = sender.peer_friendly_offer(target, slot)?;
        let recipient = self
            .sessions
            .get(&target)
            .ok_or_else(|| invalid("Friendly device recipient is unavailable."))?;
        if !recipient.peer_buff_available()
            || recipient.sector.id != sender.sector.id
            || distance(sender.position, recipient.position) > offer.range
        {
            return Err(invalid("Friendly pilot is busy or outside device range."));
        }
        let source_after = sender.energy_balance().0 - offer.cost;
        let buff = crate::peer_buffs::PeerBuff {
            revision: 0,
            source,
            target_epoch: recipient.energy_transfer_epoch(),
            template: sender.peer_friendly_template(slot)?,
            effect: offer.effect,
            value: offer.value,
            operation: offer.operation,
            expires_at_ms: crate::resources::unix_ms().saturating_add(offer.duration_ms),
        };
        self.energy_ledger
            .cast_peer_buff(source, target, epoch, sequence, source_after, buff)?;
        let balance = self.energy_ledger.balance(source).unwrap();
        if let Err(error) = self
            .sessions
            .get_mut(&source)
            .unwrap()
            .apply_energy_balance(balance)
        {
            crate::server_error!("Friendly device sender checkpoint retry for {source}: {error}");
        }
        let buff = self
            .energy_ledger
            .peer_buffs(target)
            .iter()
            .find(|buff| buff.effect == offer.effect)
            .ok_or_else(|| invalid("Committed friendly effect is unavailable."))?
            .clone();
        if let Err(error) = self
            .sessions
            .get_mut(&target)
            .unwrap()
            .apply_peer_buff(buff)
        {
            crate::server_error!(
                "Friendly device recipient checkpoint retry for {target}: {error}"
            );
        }
        self.sessions
            .get_mut(&source)
            .unwrap()
            .message(&format!("{} sent.", offer.effect.label()));
        Ok(())
    }

    /// Settle completed shield casts with one durable reactor/shield receipt.
    pub(super) fn deliver_shield_recharges(&mut self) -> io::Result<()> {
        let sources: Vec<u32> = self
            .sessions
            .iter()
            .filter_map(|(id, session)| session.pending_shield_recharge().map(|_| *id))
            .collect();
        for source in sources {
            let cast = self.sessions[&source].pending_shield_recharge().unwrap();
            if !cast.valid() {
                return Err(invalid("Invalid saved Recharge Shields cast."));
            }
            self.sessions
                .get_mut(&source)
                .unwrap()
                .checkpoint_session(true)?;
            let epoch = self.sessions[&source].shield_recharge_epoch();
            if !self.energy_ledger.recharge_seen(source, epoch, cast.effect) {
                self.update_views();
                let caster = &self.sessions[&source];
                let recipient = self.sessions.get(&cast.target);
                let eligible = recipient.is_some_and(|target| {
                    target.shield_recharge_available()
                        && caster.shield_recharge_available()
                        && target.sector.id == caster.sector.id
                        && distance(target.position, caster.position) <= cast.range
                        && caster.can_recharge_peer(cast.target)
                });
                if !eligible {
                    let caster = self.sessions.get_mut(&source).unwrap();
                    caster.clear_shield_recharge()?;
                    caster.message("Recharge Shields target left range before completion.");
                    continue;
                }
                let recipients = self.repair_recipients(source, cast.target, cast.radius);
                let targets = recipients
                    .into_iter()
                    .map(|id| {
                        let (shield, capacity) = self.sessions[&id].shield_balance();
                        let amount = if id == cast.target {
                            cast.amount
                        } else {
                            cast.splash_amount
                        };
                        (id, (shield + amount).min(capacity))
                    })
                    .collect();
                let source_energy = caster.energy_balance().0;
                self.energy_ledger.recharge_shields_area(
                    source,
                    epoch,
                    cast.effect,
                    source_energy,
                    targets,
                )?;
            }
            let energy = self.energy_ledger.balance(source).unwrap();
            if let Err(error) = self
                .sessions
                .get_mut(&source)
                .unwrap()
                .apply_energy_balance(energy)
            {
                crate::server_error!(
                    "Shield recharge sender checkpoint retry for {source}: {error}"
                );
            }
            let revision = energy.revision;
            for (&target, recipient) in &mut self.sessions {
                let Some(shield) = self
                    .energy_ledger
                    .shield_balance(target)
                    .filter(|balance| balance.revision == revision)
                else {
                    continue;
                };
                if let Err(error) = recipient.apply_shield_balance(shield) {
                    crate::server_error!(
                        "Shield recharge recipient checkpoint retry for {target}: {error}"
                    );
                }
            }
            let caster = self.sessions.get_mut(&source).unwrap();
            caster.clear_shield_recharge()?;
            caster.publish_shield_recharge(cast.target, cast.effect, cast.range == 0.);
        }
        Ok(())
    }

    /// Settle native Hull Patch and Repair Hull through the same durable world file.
    pub(super) fn deliver_hull_repairs(&mut self) -> io::Result<()> {
        let sources: Vec<u32> = self
            .sessions
            .iter()
            .filter_map(|(id, session)| session.pending_hull_repair().map(|_| *id))
            .collect();
        for source in sources {
            let cast = self.sessions[&source].pending_hull_repair().unwrap();
            if !cast.valid() {
                return Err(invalid("Invalid saved Hull Patch cast."));
            }
            self.sessions
                .get_mut(&source)
                .unwrap()
                .checkpoint_session(true)?;
            let epoch = self.sessions[&source].shield_recharge_epoch();
            if !self
                .energy_ledger
                .hull_repair_seen(source, epoch, cast.effect)
            {
                self.update_views();
                let caster = &self.sessions[&source];
                let recipient = self.sessions.get(&cast.target);
                let eligible = recipient.is_some_and(|target| {
                    target.shield_recharge_available()
                        && caster.shield_recharge_available()
                        && target.sector.id == caster.sector.id
                        && distance(target.position, caster.position) <= cast.range
                        && caster.can_recharge_peer(cast.target)
                });
                if !eligible {
                    let caster = self.sessions.get_mut(&source).unwrap();
                    caster.clear_hull_repair()?;
                    caster.message("Hull repair target left range before completion.");
                    continue;
                }
                let recipients = self.repair_recipients(source, cast.target, cast.radius);
                let targets = recipients
                    .into_iter()
                    .map(|id| {
                        let (hull, capacity) = self.sessions[&id].hull_balance();
                        let amount = if id == cast.target {
                            cast.amount
                        } else {
                            cast.splash_amount
                        };
                        (id, (hull + amount).min(capacity))
                    })
                    .collect();
                let source_energy = caster.energy_balance().0;
                self.energy_ledger.repair_hulls(
                    source,
                    epoch,
                    cast.effect,
                    source_energy,
                    targets,
                )?;
            }
            let energy = self.energy_ledger.balance(source).unwrap();
            if let Err(error) = self
                .sessions
                .get_mut(&source)
                .unwrap()
                .apply_energy_balance(energy)
            {
                crate::server_error!("Hull repair sender checkpoint retry for {source}: {error}");
            }
            let revision = energy.revision;
            for (&target, recipient) in &mut self.sessions {
                let Some(hull) = self
                    .energy_ledger
                    .hull_balance(target)
                    .filter(|balance| balance.revision == revision)
                else {
                    continue;
                };
                if let Err(error) = recipient.apply_hull_balance(hull) {
                    crate::server_error!(
                        "Hull repair recipient checkpoint retry for {target}: {error}"
                    );
                }
            }
            let caster = self.sessions.get_mut(&source).unwrap();
            caster.clear_hull_repair()?;
            caster.publish_hull_repair(cast.target, cast.effect, cast.range == 0.);
        }
        Ok(())
    }

    /// Returns:
    ///     Vec<u32>: Primary repair target and eligible nearby accepted group members.
    fn repair_recipients(&self, source: u32, primary: u32, radius: f32) -> Vec<u32> {
        let caster = &self.sessions[&source];
        let center = self.sessions[&primary].position;
        let mut recipients = vec![primary];
        if radius > 0. {
            for member in self.groups.members(source) {
                if member == primary {
                    continue;
                }
                if let Some(recipient) = self.sessions.get(&member) {
                    if recipient.shield_recharge_available()
                        && recipient.sector.id == caster.sector.id
                        && distance(recipient.position, center) <= radius
                        && caster.can_recharge_peer(member)
                    {
                        recipients.push(member);
                    }
                }
            }
        }
        recipients
    }

    /// Settle one equipment-repair stream tick across the caster and area recipients.
    pub(super) fn deliver_equipment_repairs(&mut self) -> io::Result<()> {
        let sources: Vec<u32> = self
            .sessions
            .iter()
            .filter_map(|(id, session)| session.pending_equipment_repair().map(|_| *id))
            .collect();
        for source in sources {
            let tick = self.sessions[&source].pending_equipment_repair().unwrap();
            if !tick.valid() {
                return Err(invalid("Invalid saved equipment repair tick."));
            }
            self.sessions
                .get_mut(&source)
                .unwrap()
                .checkpoint_session(true)?;
            let epoch = self.sessions[&source].shield_recharge_epoch();
            if !self
                .energy_ledger
                .equipment_repair_seen(source, epoch, tick.sequence)
            {
                self.update_views();
                let caster = &self.sessions[&source];
                let target = self.sessions.get(&tick.target);
                let eligible = target.is_some_and(|target| {
                    caster.shield_recharge_available()
                        && target.shield_recharge_available()
                        && target.sector.id == caster.sector.id
                        && distance(target.position, caster.position) <= tick.range
                        && caster.can_recharge_peer(tick.target)
                });
                if !eligible {
                    self.sessions
                        .get_mut(&source)
                        .unwrap()
                        .stop_equipment_repair("Equipment repair target left range.")?;
                    continue;
                }
                let center = self.sessions[&tick.target].position;
                let sector = caster.sector.id;
                let energy = caster.energy_balance().0;
                if energy < crate::equipment_repair::ENERGY_PER_TICK {
                    self.sessions
                        .get_mut(&source)
                        .unwrap()
                        .stop_equipment_repair("Equipment repair stopped: reactor depleted.")?;
                    continue;
                }
                let mut targets = BTreeMap::new();
                for (&id, recipient) in &self.sessions {
                    let primary = id == tick.target;
                    if !primary
                        && (tick.radius == 0.
                            || recipient.sector.id != sector
                            || !recipient.shield_recharge_available()
                            || distance(recipient.position, center) > tick.radius)
                    {
                        continue;
                    }
                    let mut equipped = recipient.equipped_for_repair();
                    let amount = crate::equipment_repair::structure_per_tick(tick.rank, primary);
                    let mut changed = false;
                    for item in equipped.iter_mut().flatten() {
                        if item.structure < 1. {
                            item.structure = (item.structure + amount).min(1.);
                            changed = true;
                        }
                    }
                    if changed {
                        targets.insert(id, equipped);
                    }
                }
                if targets.is_empty() {
                    self.sessions
                        .get_mut(&source)
                        .unwrap()
                        .stop_equipment_repair("All nearby equipment is repaired.")?;
                    continue;
                }
                self.energy_ledger.repair_equipment(
                    source,
                    epoch,
                    tick.sequence,
                    energy - crate::equipment_repair::ENERGY_PER_TICK,
                    targets,
                )?;
            }
            let balance = self.energy_ledger.balance(source).unwrap();
            self.sessions
                .get_mut(&source)
                .unwrap()
                .apply_energy_balance(balance)?;
            let recipients: Vec<u32> = self.sessions.keys().copied().collect();
            for id in recipients {
                if let Some(balance) = self.energy_ledger.equipment_balance(id) {
                    let balance = balance.clone();
                    self.sessions
                        .get_mut(&id)
                        .unwrap()
                        .apply_equipment_balance(&balance)?;
                }
            }
            self.sessions
                .get_mut(&source)
                .unwrap()
                .clear_equipment_repair_tick()?;
        }
        Ok(())
    }

    fn group_action(&mut self, source: u32, action: u32, target: u32) -> io::Result<()> {
        if (action == 11 || action == 12) && self.jumpstart_offers.contains_key(&source) {
            return self.answer_jumpstart(source, action == 11);
        }
        match action {
            10 => {
                if !self
                    .sessions
                    .get(&target)
                    .is_some_and(|session| session.logged_in)
                {
                    return Err(invalid("Invite a player who is online."));
                }
                self.groups.invite(source, target)?;
                let name = self.sessions[&source].account.character.name.clone();
                self.sessions
                    .get_mut(&target)
                    .unwrap()
                    .queue([packets::group_invite(&name)]);
                self.sessions
                    .get_mut(&source)
                    .unwrap()
                    .message("Group invitation sent.");
            }
            11 => self.groups.accept(source)?,
            12 => self.groups.decline(source),
            13 => self.groups.disband(source)?,
            14 => self.groups.leave(source),
            15 => self.groups.kick(source, target)?,
            _ => return Err(invalid("Unknown group action.")),
        }
        self.groups_dirty = true;
        Ok(())
    }

    /// Request consent before a peer Jumpstart charges the caster's reactor.
    fn jumpstart_offer(&mut self, source: u32, target: u32) -> io::Result<()> {
        if source == target {
            return self
                .sessions
                .get_mut(&source)
                .unwrap()
                .begin_jumpstart(target);
        }
        let caster = &self.sessions[&source];
        let (source_epoch, level, range) = caster.jumpstart_offer_info(target)?;
        let recipient = self
            .sessions
            .get(&target)
            .ok_or_else(|| invalid("Jumpstart target is offline."))?;
        let peer = recipient
            .peer_snapshot()
            .ok_or_else(|| invalid("Jumpstart target is offline."))?;
        if !caster.can_deliver_jumpstart(&peer, range) {
            return Err(invalid(
                "Jumpstart target is no longer in range or incapacitated.",
            ));
        }
        if self.jumpstart_offers.contains_key(&target) {
            return Err(invalid(
                "That player already has a pending Jumpstart offer.",
            ));
        }
        let message = format!(
            "Do you want to accept a jumpstart level {} from {}",
            level, caster.account.character.name
        );
        let offer = JumpstartOffer {
            source,
            source_epoch,
            target_epoch: recipient.threat().session_epoch,
            expires: Instant::now() + Duration::from_secs(30),
        };
        self.jumpstart_offers.insert(target, offer);
        self.sessions
            .get_mut(&target)
            .unwrap()
            .queue([packets::confirmation(&message)]);
        self.sessions
            .get_mut(&source)
            .unwrap()
            .message("Jumpstart offer sent.");
        Ok(())
    }

    /// Consume a recipient response and revalidate the original caster and target.
    fn answer_jumpstart(&mut self, target: u32, accepted: bool) -> io::Result<()> {
        let offer = self
            .jumpstart_offers
            .remove(&target)
            .ok_or_else(|| invalid("No pending Jumpstart offer."))?;
        let active = offer.expires > Instant::now()
            && self.sessions.get(&target).is_some_and(|session| {
                session.logged_in && session.threat().session_epoch == offer.target_epoch
            })
            && self.sessions.get(&offer.source).is_some_and(|session| {
                session.logged_in && session.threat().session_epoch == offer.source_epoch
            });
        if !active {
            return Err(invalid("Jumpstart offer expired or a player reconnected."));
        }
        if !accepted {
            self.sessions
                .get_mut(&offer.source)
                .unwrap()
                .message("Jumpstart was declined.");
            return Ok(());
        }
        let peer = self.sessions[&target]
            .peer_snapshot()
            .ok_or_else(|| invalid("Jumpstart target is no longer in space."))?;
        let caster = &self.sessions[&offer.source];
        let range = caster.jumpstart_offer_info(target)?.2;
        if !caster.can_deliver_jumpstart(&peer, range) {
            return Err(invalid("Jumpstart target is no longer eligible."));
        }
        self.sessions
            .get_mut(&offer.source)
            .unwrap()
            .begin_jumpstart(target)
    }

    fn find_online(&self, name: &str) -> io::Result<u32> {
        self.sessions
            .iter()
            .find(|(_, session)| {
                session.logged_in && session.account.character.name.eq_ignore_ascii_case(name)
            })
            .map(|(id, _)| *id)
            .ok_or_else(|| invalid("That character is not online."))
    }

    fn chat(&mut self, source: u32, channel: u8, text: &str) -> io::Result<()> {
        let text = text.trim();
        if text.is_empty() {
            return Ok(());
        }
        if let Some(command) = text.strip_prefix('/') {
            let (command, argument) = command.split_once(' ').unwrap_or((command, ""));
            let argument = argument.trim();
            match command.to_ascii_lowercase().as_str() {
                "d_spawn" => return self.developer_spawn(source, argument),
                "d_jump" => return self.developer_jump(source, argument),
                "launch" => return self.sessions.get_mut(&source).unwrap().launch_from_station(),
                "tow" => return self.sessions.get_mut(&source).unwrap().tow_to_station(),
                "jumpstart" => return self.sessions.get_mut(&source).unwrap().self_jumpstart(),
                "call" => return self.offer_call_forward(source, argument),
                "acceptcall" => return self.answer_call_forward(source, true),
                "declinecall" => return self.answer_call_forward(source, false),
                "equip" => return self.sessions.get_mut(&source).unwrap().equip_cargo(argument),
                "unequip" => return self.sessions.get_mut(&source).unwrap().unequip(argument),
                "invite" => return self.group_action(source, 10, self.find_online(argument)?),
                "accept" => return self.group_action(source, 11, 0),
                "decline" => return self.group_action(source, 12, 0),
                "leave" => return self.group_action(source, 14, 0),
                "disband" => return self.group_action(source, 13, 0),
                "kick" => return self.group_action(source, 15, self.find_online(argument)?),
                "ability" => {
                    let index: u32 = argument.parse().map_err(|_| invalid("Use /ability followed by an ability number."))?;
                    return self.sessions.get_mut(&source).unwrap().handle_input(0x58,
                        &[source.to_le_bytes(), 0u32.to_le_bytes(), index.to_le_bytes()].concat());
                }
                "who" => {
                    let names = self.sessions.values().filter(|session| session.logged_in)
                        .map(|session| session.account.character.name.clone()).collect::<Vec<_>>().join(", ");
                    self.sessions.get_mut(&source).unwrap().message(&format!("Online: {names}"));
                }
                "where" => {
                    let session = &self.sessions[&source];
                    let message = format!("{}: {:.0}, {:.0}, {:.0}", session.sector.name, session.position[0], session.position[1], session.position[2]);
                    self.sessions.get_mut(&source).unwrap().message(&message);
                }
                "help" => self.sessions.get_mut(&source).unwrap().message("Commands: /who, /where, /invite NAME, /accept, /decline, /leave, /kick NAME, /disband, /ability NUMBER, /call SKILL NAME, /acceptcall, /declinecall, /jumpstart, /tow, /launch, /equip CARGO_SLOT MOUNT_SLOT, /unequip MOUNT_SLOT. Dev: /d_spawn [MOB_NAME_OR_ID] [COUNT], /d_jump SECTOR_NAME_OR_ID. Slots are zero-based."),
                _ => return Err(invalid("Unknown local command. Use /help.")),
            }
            return Ok(());
        }
        let session = &self.sessions[&source];
        let name = session.account.character.name.clone();
        let sector = session.sector.id;
        let position = session.position;
        let recipients: Vec<u32> = match channel {
            0 => {
                // Legacy "to target" chat, rendered as a private message.
                let target = session.current_target();
                if !self
                    .sessions
                    .get(&target)
                    .is_some_and(|session| session.logged_in)
                {
                    return Err(invalid("Select an online player for private chat."));
                }
                vec![source, target]
            }
            1 => {
                let members = self.groups.members(source);
                if members.is_empty() {
                    return Err(invalid("You are not in a group."));
                }
                members
            }
            2 => return Err(invalid("Guilds are not implemented yet.")),
            _ => self
                .sessions
                .iter()
                .filter(|(_, session)| {
                    session.logged_in
                        && session.sector.id == sector
                        && (channel == 4 || distance(session.position, position) <= 50_000.)
                })
                .map(|(id, _)| *id)
                .collect(),
        };
        let prefix = if channel == 0 {
            "[PM] "
        } else if channel == 1 {
            "[Group] "
        } else {
            ""
        };
        for id in recipients {
            self.sessions
                .get_mut(&id)
                .unwrap()
                .message(&format!("{prefix}{name}: {text}"));
        }
        Ok(())
    }

    pub(super) fn publish_groups(&mut self) {
        let updates: Vec<_> = self
            .sessions
            .keys()
            .map(|id| {
                let group = self.groups.group(*id);
                let members = self
                    .groups
                    .members(*id)
                    .iter()
                    .filter_map(|member| self.sessions.get(member))
                    .map(|session| session.account.character.clone())
                    .collect::<Vec<_>>();
                (
                    *id,
                    packets::group_state(
                        *id,
                        &members,
                        group.map_or(0, |group| group.leader),
                        group.map_or(0, |group| group.formation),
                        group.map_or(&[], |group| group.formed.as_slice()),
                    ),
                )
            })
            .collect();
        for (id, frame) in updates {
            if self.sessions[&id].logged_in {
                self.sessions.get_mut(&id).unwrap().queue([frame]);
            }
        }
    }

    pub(super) fn save_groups(&self) -> io::Result<()> {
        std::fs::create_dir_all(&self.root)?;
        let temporary = self.root.join("groups.local.json.tmp");
        std::fs::write(&temporary, serde_json::to_vec_pretty(&self.groups)?)?;
        std::fs::rename(temporary, self.root.join("groups.local.json"))
    }

    pub(super) fn advance_formations(&mut self) {
        // The original group handler starts the drive on every formed ship.
        // Keep native follower phases and relative positions synchronized with
        // the leader, including on the first tick after a short replacement.
        let mut following = std::collections::HashSet::new();
        for group in self.groups.formation_groups() {
            let leader = self
                .sessions
                .get(&group.leader)
                .filter(|session| session.can_lead_formation())
                .and_then(|session| session.peer_snapshot());
            let leader_warping = self
                .sessions
                .get(&group.leader)
                .is_some_and(|session| session.formation_warp().active());
            let mut group_speed = self
                .sessions
                .get(&group.leader)
                .map_or(0., |session| session.formation_warp_speed());
            if leader_warping {
                for member in group
                    .formed
                    .iter()
                    .copied()
                    .filter(|id| *id != group.leader)
                {
                    if self.sessions.get(&member).is_some_and(|session| {
                        leader.as_ref().is_some_and(|leader| {
                            session.can_follow_formation(true) && session.sector.id == leader.sector
                        })
                    }) {
                        group_speed =
                            group_speed.min(self.sessions[&member].formation_warp_speed());
                    }
                }
            }
            if let Some(session) = self.sessions.get_mut(&group.leader) {
                session.set_formation_warp_speed(group_speed);
            }
            let leader_warp = self
                .sessions
                .get(&group.leader)
                .map(|session| session.formation_warp());
            for member in group
                .formed
                .iter()
                .copied()
                .filter(|id| *id != group.leader)
            {
                let eligible = leader.as_ref().is_some_and(|leader| {
                    self.sessions.get(&member).is_some_and(|session| {
                        session.can_follow_formation(leader_warping)
                            && session.sector.id == leader.sector
                    })
                });
                if eligible {
                    let index = group.members.iter().position(|id| *id == member).unwrap();
                    let offset = crate::groups::formation_offset(group.formation, index);
                    let session = self.sessions.get_mut(&member).unwrap();
                    if let Err(error) = session.sync_formation_warp(
                        leader.as_ref().unwrap(),
                        leader_warp.as_ref().unwrap(),
                    ) {
                        session.message(&error.to_string());
                        self.groups.leave_formation(member);
                        self.groups_dirty = true;
                    } else {
                        session.follow_formation(leader.as_ref().unwrap(), offset);
                        following.insert(member);
                    }
                } else {
                    self.groups.leave_formation(member);
                    self.groups_dirty = true;
                }
            }
        }
        for (id, session) in &mut self.sessions {
            if !following.contains(id) {
                session.release_formation();
            }
        }
    }

    pub(super) fn deliver_support(&mut self) -> io::Result<()> {
        let actions: Vec<_> = self
            .sessions
            .iter_mut()
            .map(|(id, session)| {
                (
                    *id,
                    session.sector.id,
                    session.position,
                    session.take_support(),
                )
            })
            .collect();
        for (source, sector, position, actions) in actions {
            for action in actions {
                let mut delivered = false;
                // Native Jumpstart can rescue any visible incapacitated ship,
                // independently of group membership. Other support stays scoped
                // to the source's actual group.
                let recipients = match action {
                    SupportAction::Jumpstart { target, .. } => vec![target],
                    _ => self.groups.members(source),
                };
                for id in recipients {
                    if id == source {
                        continue;
                    }
                    let Some(peer) = self
                        .sessions
                        .get(&id)
                        .and_then(PlayerSession::peer_snapshot)
                    else {
                        continue;
                    };
                    let eligible = match action {
                        SupportAction::Wormhole {
                            origin_sector,
                            origin,
                            ..
                        } => {
                            peer.sector == origin_sector && distance(peer.position, origin) <= 6000.
                        }
                        SupportAction::Cloak { enabled: false, .. } => true,
                        SupportAction::Cloak { .. } => {
                            peer.sector == sector && distance(peer.position, position) <= 1000.
                        }
                        SupportAction::Environment { target, .. } => {
                            (target == 0 || target == id)
                                && peer.sector == sector
                                && distance(peer.position, position) <= 3000.
                        }
                        SupportAction::Jumpstart { target, range, .. } => {
                            target == id
                                && self.sessions[&source].can_deliver_jumpstart(&peer, range)
                        }
                        SupportAction::Fold { target } => {
                            target == id
                                && peer.sector == sector
                                && distance(peer.position, position) <= 6000.
                        }
                        SupportAction::Energy { .. } => {
                            peer.sector == sector && distance(peer.position, position) <= 3000.
                        }
                    };
                    if eligible {
                        self.sessions
                            .get_mut(&id)
                            .unwrap()
                            .receive_support(source, &action)?;
                        delivered = true;
                    }
                }
                if !delivered
                    && matches!(
                        action,
                        SupportAction::Jumpstart { .. } | SupportAction::Fold { .. }
                    )
                {
                    self.sessions.get_mut(&source).unwrap().message(
                        if matches!(action, SupportAction::Jumpstart { .. }) {
                            "The target ship left Jumpstart range or no longer needs recovery."
                        } else {
                            "The group member left ability range before completion."
                        },
                    );
                }
            }
        }
        Ok(())
    }

    pub(super) fn expire_group_cloak(&mut self) -> io::Result<()> {
        let peers = self.snapshots();
        let expired: Vec<_> = self
            .sessions
            .iter()
            .filter_map(|(id, session)| session.cloak_parent().map(|source| (*id, source)))
            .filter(|(id, source)| {
                !self.groups.together(*id, *source)
                    || !peers
                        .iter()
                        .find(|peer| peer.profile.game_id == *source)
                        .is_some_and(|leader| {
                            leader.cloaked
                                && leader.hull > 0.
                                && self.sessions[id].sector.id == leader.sector
                                && distance(self.sessions[id].position, leader.position) <= 1000.
                        })
            })
            .collect();
        for (id, source) in expired {
            self.sessions.get_mut(&id).unwrap().receive_support(
                source,
                &SupportAction::Cloak {
                    enabled: false,
                    rank: 0,
                    stealth: 0,
                },
            )?;
        }
        // Legacy checkpoints did not retain caster-derived group stealth.
        // Refresh it before NPC targeting instead of using a recipient's own
        // (possibly absent) trained Cloak skill.
        let inherited: Vec<_> = self
            .sessions
            .iter()
            .filter_map(|(id, session)| {
                let source = session.cloak_parent()?;
                Some((*id, source, self.sessions.get(&source)?.cloak_stealth()?))
            })
            .collect();
        for (id, source, stealth) in inherited {
            self.sessions
                .get_mut(&id)
                .unwrap()
                .inherit_cloak_stealth(source, stealth);
        }
        Ok(())
    }
}
