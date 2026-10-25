//! One catalog-backed targeted friendly buff with an atomic sender-energy
//! receipt. Broader simultaneous status stacking remains a separate system.
use crate::items::{ItemCatalog, ItemTemplate, Modifier};
use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug)]
pub struct ScanOffer {
    pub cost: f32,
    pub range: f32,
    pub value: f32,
    pub duration_ms: u64,
    pub visual: u16,
}

/// Returns:
///     Option<ScanOffer>: Bounded targeted-friendly scan effect from public data.
pub fn scan_offer(template: &ItemTemplate) -> Option<ScanOffer> {
    let effect = template.activated.first()?;
    if template.item_type != 11
        || template.activated.len() != 1
        || effect.description != "Scan Boost (Activated)"
        || effect.flag1 != 16
        || effect.flag2 != 1
        || effect.modifiers.len() != 2
        || !template.activation.energy.is_finite()
        || !(0. ..=10_000.).contains(&template.activation.energy)
        || !template.activation.range.is_finite()
        || !(1. ..=10_000.).contains(&template.activation.range)
    {
        return None;
    }
    let scan = effect
        .modifiers
        .iter()
        .find(|modifier| modifier.stat == "STAT_SCAN_RANGE")?;
    let timer = effect
        .modifiers
        .iter()
        .find(|modifier| modifier.stat == "NO_STAT")?;
    if scan.operation != 2
        || !scan.value.is_finite()
        || !(0. ..=100.).contains(&scan.value)
        || timer.operation != 5
        || !timer.value.is_finite()
        || !(1. ..=3600.).contains(&timer.value)
    {
        return None;
    }
    Some(ScanOffer {
        cost: template.activation.energy,
        range: template.activation.range,
        value: scan.value,
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
    pub value: f32,
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
                .and_then(scan_offer)
                .is_some_and(|offer| {
                    (offer.value - self.value).abs() < 0.001
                        && self.expires_at_ms <= now_ms.saturating_add(offer.duration_ms)
                })
    }

    /// Returns:
    ///     Modifier: Frozen scan-range effect applied to the recipient.
    pub fn modifier(&self) -> Modifier {
        Modifier {
            stat: "STAT_SCAN_RANGE".into(),
            operation: 2,
            value: self.value,
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
    fn public_scan_devices_have_one_targeted_friendly_rule() {
        let items = ItemCatalog::load().unwrap();
        for id in [1813, 1828, 1864, 2500] {
            let offer = scan_offer(items.get(id).unwrap()).unwrap();
            assert!(offer.range > 0. && offer.duration_ms > 0);
        }
        assert!(scan_offer(items.get(2412).unwrap()).is_none());
    }
}
