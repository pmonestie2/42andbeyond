//! Catalog-backed targeted friendly buffs with atomic sender-energy receipts.
//! Broader simultaneous status stacking remains a separate system.
use crate::items::{ItemCatalog, ItemTemplate, Modifier};
use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
pub enum FriendlyEffect {
    #[default]
    ScanBoost,
    RechargeShields,
    RechargeReactor,
    ReduceSignature,
}

impl FriendlyEffect {
    /// Returns:
    ///     &str: Recipient-facing native effect label.
    pub fn label(self) -> &'static str {
        match self {
            Self::ScanBoost => "Scan Boost",
            Self::RechargeShields => "Recharge Shields",
            Self::RechargeReactor => "Recharge Reactor",
            Self::ReduceSignature => "Reduce Ship Signature",
        }
    }

    /// Returns:
    ///     &str: Authoritative ship stat changed by this effect.
    pub fn stat(self) -> &'static str {
        match self {
            Self::ScanBoost => "STAT_SCAN_RANGE",
            Self::RechargeShields => "STAT_SHIELD_RECHARGE",
            Self::RechargeReactor => "STAT_ENERGY_RECHARGE",
            Self::ReduceSignature => "STAT_SIGNATURE",
        }
    }

    /// Returns:
    ///     usize: Native ShipIndex.Buffs slot reserved for this ally effect.
    pub fn slot(self) -> usize {
        match self {
            Self::ScanBoost => 1,
            Self::RechargeShields => 2,
            Self::RechargeReactor => 3,
            Self::ReduceSignature => 4,
        }
    }
}

#[derive(Clone, Copy, Debug)]
pub struct FriendlyOffer {
    pub effect: FriendlyEffect,
    pub cost: f32,
    pub range: f32,
    pub value: f32,
    pub operation: u32,
    pub duration_ms: u64,
    pub visual: u16,
}

/// Returns:
///     Option<FriendlyOffer>: Bounded targeted-friendly catalog effect.
pub fn friendly_offer(template: &ItemTemplate) -> Option<FriendlyOffer> {
    let effect = template.activated.first()?;
    let (kind, flag, maximum) = match effect.description.as_str() {
        "Scan Boost (Activated)" => (FriendlyEffect::ScanBoost, 16, 100.),
        "Recharge Shields (Activated)" => (FriendlyEffect::RechargeShields, 64, 1000.),
        "Recharge Reactor (Activated)" => (FriendlyEffect::RechargeReactor, 64, 1000.),
        "Reduce Ship Signature (Activated)" => (FriendlyEffect::ReduceSignature, 16, 100.),
        _ => return None,
    };
    if template.item_type != 11
        || template.activated.len() != 1
        || effect.flag1 != flag
        || effect.flag2 != 1
        || effect.modifiers.len() != 2
        || !template.activation.energy.is_finite()
        || !(0. ..=10_000.).contains(&template.activation.energy)
        || !template.activation.range.is_finite()
        || !(1. ..=10_000.).contains(&template.activation.range)
    {
        return None;
    }
    let stat = effect
        .modifiers
        .iter()
        .find(|modifier| modifier.stat == kind.stat())?;
    let timer = effect
        .modifiers
        .iter()
        .find(|modifier| modifier.stat == "NO_STAT")?;
    if !(match kind {
        FriendlyEffect::ScanBoost | FriendlyEffect::RechargeShields => stat.operation == 2,
        FriendlyEffect::RechargeReactor => matches!(stat.operation, 1 | 2),
        FriendlyEffect::ReduceSignature => stat.operation == 4,
    }) || !stat.value.is_finite()
        || !(0. ..=maximum).contains(&stat.value)
        || timer.operation != 5
        || !timer.value.is_finite()
        || !(1. ..=3600.).contains(&timer.value)
    {
        return None;
    }
    Some(FriendlyOffer {
        effect: kind,
        cost: template.activation.energy,
        range: template.activation.range,
        value: stat.value,
        operation: stat.operation,
        duration_ms: (timer.value * 1000.) as u64,
        visual: u16::try_from(effect.visual).ok()?,
    })
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PeerBuff {
    pub revision: u64,
    pub source: u32,
    pub target_epoch: u64,
    pub template: u32,
    #[serde(default)]
    pub effect: FriendlyEffect,
    pub value: f32,
    #[serde(default)]
    pub operation: u32,
    pub expires_at_ms: u64,
}

impl PeerBuff {
    /// Returns:
    ///     bool: Whether the durable receipt is bounded and matches catalog data.
    pub fn valid(&self, items: &ItemCatalog, now_ms: u64, epoch: u64) -> bool {
        self.revision != 0
            && self.source != 0
            && self.target_epoch == epoch
            && self.expires_at_ms > now_ms
            && self.value.is_finite()
            && items
                .get(self.template)
                .ok()
                .and_then(friendly_offer)
                .is_some_and(|offer| {
                    offer.effect == self.effect
                        && offer.operation == self.effective_operation()
                        && (offer.value - self.value).abs() < 0.001
                        && self.expires_at_ms <= now_ms.saturating_add(offer.duration_ms)
                })
    }

    /// Returns:
    ///     Modifier: Frozen catalog effect applied to the recipient.
    pub fn modifier(&self) -> Modifier {
        Modifier {
            stat: self.effect.stat().into(),
            operation: self.effective_operation(),
            value: self.value,
        }
    }

    /// Returns:
    ///     u32: Stored operation or the old Scan Boost checkpoint default.
    fn effective_operation(&self) -> u32 {
        if self.operation == 0 && self.effect == FriendlyEffect::ScanBoost {
            2
        } else {
            self.operation
        }
    }

    /// Returns:
    ///     u32: Stable native visual identity across server replacement.
    pub fn visual_id(&self, target: u32) -> u32 {
        (self.revision as u32) ^ target ^ 0x5ca0_0000
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn public_friendly_devices_have_bounded_recharge_scan_and_signature_rules() {
        let items = ItemCatalog::load().unwrap();
        for id in [1813, 1828, 1864, 2500] {
            let offer = friendly_offer(items.get(id).unwrap()).unwrap();
            assert!(offer.range > 0. && offer.duration_ms > 0);
            assert_eq!(offer.effect, FriendlyEffect::ScanBoost);
        }
        for (id, kind) in [
            (1805, FriendlyEffect::RechargeShields),
            (1810, FriendlyEffect::RechargeReactor),
        ] {
            let offer = friendly_offer(items.get(id).unwrap()).unwrap();
            assert_eq!(offer.effect, kind);
            assert!(offer.duration_ms > 0);
        }
        for id in [1945, 2027, 2119, 2209, 2300, 2403, 2494] {
            let offer = friendly_offer(items.get(id).unwrap()).unwrap();
            assert_eq!(offer.effect, FriendlyEffect::ReduceSignature);
            assert_eq!(offer.operation, 4);
            assert_eq!(offer.value, 25.);
            assert_eq!(offer.visual, 0);
        }
        assert!(friendly_offer(items.get(2412).unwrap()).is_none());
    }

    #[test]
    fn old_scan_checkpoint_without_effect_kind_remains_valid() {
        let items = ItemCatalog::load().unwrap();
        let now = crate::resources::unix_ms();
        let old = serde_json::json!({
            "revision": 1,
            "source": 0x40000001u32,
            "target_epoch": 7,
            "template": 1813,
            "value": 25.0,
            "expires_at_ms": now + 10_000,
        });
        let buff: PeerBuff = serde_json::from_value(old).unwrap();
        assert!(buff.valid(&items, now, 7));
        assert_eq!(buff.effect, FriendlyEffect::ScanBoost);
        assert_eq!(buff.modifier().operation, 2);
    }
}
