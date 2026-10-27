//! Native production terminals. Inventory owns durable pending jobs and samples;
//! the window only owns selection/filter/display state. No components or money
//! disappear during the timer: completion commits all inputs and output together.
use super::*;
use crate::crafting::{CraftJob, CraftOperation, CraftRecipe, CRAFT_DELAY_MS};
use crate::production::{ProductionRequest, WorkbenchView};
use crate::refining::{RefineJob, RefineryCatalog, REFINE_DELAY_MS, REFINE_PRICE};
use crate::resources::unix_ms;
use std::collections::BTreeMap;

fn refinery_mode() -> u32 {
    4
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub(super) struct WorkbenchWindow {
    #[serde(default = "refinery_mode")]
    mode: u32,
    #[serde(default)]
    category: u32,
    station: u32,
    selected: u32,
    filter: u32,
    completed: bool,
    #[serde(default)]
    #[serde(alias = "analysis_failed")]
    operation_failed: bool,
    #[serde(default)]
    recovered_components: Vec<u32>,
    #[serde(default)]
    known_count: usize,
    #[serde(default)]
    page: u32,
    #[serde(skip)]
    last_view: Option<WorkbenchView>,
}

impl WorkbenchWindow {
    pub(super) fn valid(&self, station: u32) -> bool {
        self.station == station
            && self.filter & !0x1ff == 0
            && self.known_count <= 500
            && self.page
                < crate::inventory::MAX_KNOWN_FORMULAS
                    .div_ceil(crate::production::FORMULA_PAGE_SIZE) as u32
            && self.recovered_components.len() <= 6
            && matches!(self.mode, 1..=4)
            && crate::production::valid_category(self.category)
            && (self.selected == 0
                || if self.mode == 4 {
                    RefineryCatalog::load().is_ok_and(|catalog| catalog.get(self.selected).is_ok())
                } else {
                    ItemCatalog::load().is_ok_and(|items| items.get(self.selected).is_ok())
                })
    }
}

impl PlayerSession {
    /// Select a bounded page of native formula slots without changing the selected recipe.
    /// Raises:
    ///     io::Error: The request owner, page, or active terminal mode is invalid.
    pub(super) fn workbench_page_input(&mut self, body: &[u8]) -> io::Result<()> {
        self.check_workbench_station()?;
        if body.len() != 8 {
            return Err(invalid("Invalid formula page request length."));
        }
        let mut reader = Reader::new(body);
        let owner = reader.u32()?;
        let page = reader.u32()?;
        if owner != self.player_id()
            || page
                >= crate::inventory::MAX_KNOWN_FORMULAS
                    .div_ceil(crate::production::FORMULA_PAGE_SIZE) as u32
        {
            return Err(invalid("Invalid formula page owner or index."));
        }
        let window = self
            .workbench
            .as_mut()
            .filter(|window| window.mode == 1)
            .ok_or_else(|| invalid("Open manufacturing before selecting a recipe page."))?;
        window.page = page;
        self.refresh_workbench(true)
    }

    pub(super) fn refresh_workbench_target(&mut self) {
        if let Some(window) = &mut self.workbench {
            if matches!(window.mode, 2 | 3) {
                window.selected = self.inventory.production_target[0]
                    .as_ref()
                    .map_or(0, |item| item.template);
                window.completed = false;
                window.operation_failed = false;
                window.recovered_components.clear();
            }
        }
    }
    pub(super) fn workbench_lab_id(&self) -> u32 {
        self.player_id() | 0x80000000
    }

    fn check_workbench_station(&self) -> io::Result<()> {
        if !self.logged_in || self.sector.station.is_none() || self.hull <= 0. {
            return Err(invalid("Enter a station alive before using a workbench."));
        }
        Ok(())
    }

    pub(super) fn initialise_workbench_lab(&mut self, create: bool) -> io::Result<()> {
        if self.sector.station.is_none() {
            self.workbench = None;
            self.workbench_lab_ready = false;
            return Ok(());
        }
        let lab = self.workbench_lab_id();
        self.pending
            .push(Frame::new(0x7f, lab.to_be_bytes().to_vec()));
        if create {
            self.pending.push(packets::manufacturing_lab(lab));
        }
        self.workbench_lab_ready = true;
        if self
            .workbench
            .as_ref()
            .is_some_and(|window| window.station != self.sector.id)
        {
            self.workbench = None;
        }
        self.refresh_workbench(true)
    }

    pub(super) fn workbench_input(&mut self, opcode: u16, body: &[u8]) -> io::Result<()> {
        self.check_workbench_station()?;
        if body.len() != if opcode == 0x80 { 9 } else { 8 } {
            return Err(invalid("Invalid workbench request length."));
        }
        let mut reader = Reader::new(body);
        let owner = reader.be_u32()?;
        let own_ids = [
            self.player_id(),
            self.player_id() & 0xffffff,
            self.workbench_lab_id(),
        ];
        // Macro captures use network order; the native C++ handler did not
        // inspect this field. Accept either byte order only for this owner.
        if !own_ids.contains(&owner) && !own_ids.contains(&owner.swap_bytes()) {
            return Err(invalid("Production request owner mismatch."));
        }
        if opcode == 0x80 {
            let enabled = reader.take(1)?[0];
            let mask = reader.be_u32()?;
            if enabled > 1 || mask & !0x1ff != 0 {
                return Err(invalid("Invalid workbench level filter."));
            }
            let window = self
                .workbench
                .as_mut()
                .ok_or_else(|| invalid("Open a workbench first."))?;
            window.page = 0;
            if enabled == 1 {
                window.filter |= mask;
            } else {
                window.filter &= !mask;
            }
        } else {
            let value = reader.be_u32()?;
            match opcode {
                0x79 if matches!(value, 1..=4) => {
                    if self.inventory.craft_job.is_some() || self.inventory.refine_job.is_some() {
                        return Err(invalid("Wait for the current production operation."));
                    }
                    if matches!(value, 1 | 4) && self.inventory.production_target[0].is_some() {
                        return Err(invalid(
                            "Return the target item to cargo before switching terminals.",
                        ));
                    }
                    self.close_npc();
                    self.workbench = Some(WorkbenchWindow {
                        mode: value,
                        category: 0,
                        station: self.sector.id,
                        selected: self
                            .inventory
                            .refine_job
                            .as_ref()
                            .map_or(0, |job| job.product),
                        filter: 0x1ff,
                        completed: false,
                        operation_failed: false,
                        recovered_components: Vec::new(),
                        known_count: 0,
                        page: 0,
                        last_view: None,
                    });
                }
                0x79 if value == 0 => {
                    self.cancel_production()?;
                }
                0x79 => return Err(invalid("This production terminal mode is not implemented.")),
                0x7b => {
                    if self.inventory.craft_job.is_some() || self.inventory.refine_job.is_some() {
                        return Err(invalid("Wait for the current production operation."));
                    }
                    let product = if CraftRecipe::resolve(value, &self.items).is_ok() {
                        value
                    } else {
                        value.swap_bytes()
                    };
                    let recipe = CraftRecipe::resolve(product, &self.items)?;
                    recipe.check_skill(&self.skill_catalog, &self.items)?;
                    if !self.inventory.known_formulas.contains(&product) {
                        return Err(invalid("This recipe has not been learned."));
                    }
                    let window = self
                        .workbench
                        .as_mut()
                        .filter(|window| window.mode == 1)
                        .ok_or_else(|| invalid("Open a manufacturing terminal first."))?;
                    window.selected = product;
                    window.completed = false;
                }
                0x7c => {
                    if self.inventory.refine_job.is_some() || self.inventory.craft_job.is_some() {
                        return Err(invalid("Wait for the current refining operation."));
                    }
                    let catalog = RefineryCatalog::load()?;
                    // Old native handlers accepted either item-ID byte order;
                    // real IDs fit u16, so this cannot select two valid recipes.
                    let product = if catalog.get(value).is_ok() {
                        value
                    } else {
                        value.swap_bytes()
                    };
                    catalog.get(product)?;
                    let window = self
                        .workbench
                        .as_mut()
                        .filter(|window| window.mode == 4)
                        .ok_or_else(|| invalid("Open a refinery first."))?;
                    window.selected = product;
                    window.completed = false;
                }
                0x7a => {
                    if !crate::production::valid_category(value) {
                        return Err(invalid("Unknown manufacturing category."));
                    }
                    let window = self
                        .workbench
                        .as_mut()
                        .ok_or_else(|| invalid("Open a terminal first."))?;
                    if window.mode == 1 {
                        window.category = value;
                        window.page = 0;
                    }
                }
                0x7e if value == 0 => {
                    self.cancel_production()?;
                }
                0x7e if value == 2 || value == 3 => {
                    if self
                        .workbench
                        .as_ref()
                        .is_some_and(|window| window.mode == 4)
                    {
                        self.start_refining(value == 3)?;
                    } else if value == 2 {
                        self.start_crafting()?;
                    } else {
                        return Err(invalid("Stack production currently requires the refinery."));
                    }
                }
                _ => return Err(invalid("Unsupported workbench action.")),
            }
        }
        self.refresh_workbench(true)
    }

    fn cancel_production(&mut self) -> io::Result<()> {
        if self.inventory.refine_job.is_some() || self.inventory.craft_job.is_some() {
            let mut candidate = self.inventory.clone();
            candidate.refine_job = None;
            candidate.craft_job = None;
            self.commit_inventory(candidate)?;
            crate::server_log!(
                "Production cancelled: {}; terminal closed; no materials or credits consumed",
                self.account.character.name
            );
        }
        self.workbench = None;
        Ok(())
    }

    fn start_refining(&mut self, stack: bool) -> io::Result<()> {
        if self.inventory.refine_job.is_some() || self.inventory.craft_job.is_some() {
            return Err(invalid("Refining is already in progress."));
        }
        let window = self
            .workbench
            .as_ref()
            .ok_or_else(|| invalid("Open a workbench first."))?;
        let catalog = RefineryCatalog::load()?;
        let recipe = catalog.get(window.selected)?;
        let unit_cost = self.negotiated_service_price(REFINE_PRICE)?;
        let count = if stack {
            recipe.max_iterations_for_cost(&self.inventory, &self.items, unit_cost)
        } else {
            1
        };
        recipe.apply(&self.inventory, count, unit_cost, &self.items)?;
        let request = ProductionRequest {
            epoch: self.session_epoch,
            sequence: self.inputs.back().copied().unwrap_or(0),
        };
        if !request.follows(self.inventory.last_production_request) {
            return Err(invalid(
                "Refinery request was already processed; no new job started.",
            ));
        }
        let mut candidate = self.inventory.clone();
        candidate.last_production_request = Some(request);
        candidate.refine_job = Some(RefineJob {
            station: self.sector.id,
            product: recipe.output,
            iterations: count,
            unit_cost,
            due_ms: unix_ms().saturating_add(REFINE_DELAY_MS),
            request,
            recipe: Some(recipe.clone()),
        });
        self.commit_inventory(candidate)?;
        if let Some(window) = &mut self.workbench {
            window.completed = false;
        }
        crate::server_log!(
            "Refining started: {}; product={}; iterations={count}; unit_cost={unit_cost}",
            self.account.character.name,
            recipe.output
        );
        self.pending.push(packets::message("Refining in progress."));
        Ok(())
    }

    /// Target container 12 is owned escrow, never a client-supplied template.
    /// Native components/override containers remain unsupported until their
    /// transactions are implemented; they cannot become free build inputs.
    pub(super) fn check_workbench_transfer(&self, transfer: ItemTransfer) -> io::Result<()> {
        if transfer.source != 12 && transfer.destination != 12 {
            return Ok(());
        }
        self.check_workbench_station()?;
        if self.inventory.craft_job.is_some() || self.inventory.refine_job.is_some() {
            return Err(invalid(
                "Wait for production before moving an analyzer sample.",
            ));
        }
        if transfer.quantity != 1 {
            return Err(invalid("The analyzer accepts one item at a time."));
        }
        if transfer.destination == 12 {
            if !self
                .workbench
                .as_ref()
                .is_some_and(|window| matches!(window.mode, 2 | 3))
                || self.inventory.production_target[0].is_some()
            {
                return Err(invalid("Open an empty analyzer before inserting an item."));
            }
            let sample = self
                .inventory
                .cargo
                .get(transfer.source_slot)
                .and_then(Option::as_ref)
                .ok_or_else(|| invalid("Analyzer source is not owned cargo."))?;
            CraftRecipe::resolve(sample.template, &self.items)?
                .check_skill(&self.skill_catalog, &self.items)?;
        } else if self
            .inventory
            .cargo
            .get(transfer.destination_slot)
            .is_none_or(Option::is_some)
        {
            return Err(invalid(
                "Return the analyzer sample to an empty cargo slot.",
            ));
        }
        Ok(())
    }

    fn start_crafting(&mut self) -> io::Result<()> {
        if self.inventory.refine_job.is_some() || self.inventory.craft_job.is_some() {
            return Err(invalid("Production is already in progress."));
        }
        let window = self
            .workbench
            .as_ref()
            .ok_or_else(|| invalid("Open a production terminal first."))?;
        let operation = match window.mode {
            1 => CraftOperation::Manufacture,
            2 => CraftOperation::Analyze,
            3 => CraftOperation::Dismantle,
            _ => return Err(invalid("Unsupported production operation.")),
        };
        let sample = if operation != CraftOperation::Manufacture {
            Some(
                self.inventory.production_target[0]
                    .clone()
                    .ok_or_else(|| invalid("Place an item in the analyzer."))?,
            )
        } else {
            None
        };
        let product = sample
            .as_ref()
            .map_or(window.selected, |sample| sample.template);
        let mut recipe = CraftRecipe::resolve(product, &self.items)?;
        recipe.cost = self.negotiated_service_price(recipe.cost)?;
        let recovered_components = if operation == CraftOperation::Dismantle {
            let chance = recipe.dismantle_chance_with_equipment(
                &self.inventory,
                &self.skill_catalog,
                &self.items,
            )?;
            recipe
                .components
                .iter()
                .copied()
                .filter(|_| rand::random::<u32>() % 100 < chance)
                .collect::<Vec<_>>()
        } else {
            Vec::new()
        };
        let analysis_succeeded = if operation == CraftOperation::Analyze {
            recipe.analyze(&self.inventory, &self.skill_catalog, &self.items, true)?;
            rand::random::<u32>() % 100
                < recipe.chance_with_equipment(&self.inventory, &self.skill_catalog, &self.items)?
        } else if operation == CraftOperation::Dismantle {
            recipe.dismantle(
                &self.inventory,
                &self.skill_catalog,
                &self.items,
                &recovered_components,
            )?;
            false
        } else {
            recipe.manufacture(
                &self.inventory,
                &self.skill_catalog,
                &self.items,
                &self.account.character.name,
            )?;
            true
        };
        let request = ProductionRequest {
            epoch: self.session_epoch,
            sequence: self.inputs.back().copied().unwrap_or(0),
        };
        if !request.follows(self.inventory.last_production_request) {
            return Err(invalid("Production request was already processed."));
        }
        let mut candidate = self.inventory.clone();
        candidate.last_production_request = Some(request);
        candidate.craft_job = Some(CraftJob {
            station: self.sector.id,
            operation,
            recipe,
            due_ms: unix_ms().saturating_add(CRAFT_DELAY_MS),
            request,
            sample,
            analysis_succeeded,
            builder_name: if operation == CraftOperation::Manufacture {
                self.account.character.name.clone()
            } else {
                String::new()
            },
            recovered_components,
        });
        self.commit_inventory(candidate)?;
        if let Some(window) = &mut self.workbench {
            window.selected = product;
            window.completed = false;
            window.operation_failed = false;
            window.recovered_components.clear();
        }
        crate::server_log!(
            "Production started: {}; operation={operation:?}; product={product}",
            self.account.character.name
        );
        self.pending
            .push(packets::message("Production in progress."));
        Ok(())
    }

    fn advance_crafting(&mut self) -> io::Result<()> {
        let Some(job) = self.inventory.craft_job.clone() else {
            return Ok(());
        };
        if job.station == self.sector.id && unix_ms() < job.due_ms {
            return Ok(());
        }
        let result = if job.station != self.sector.id {
            Err(invalid("The ship left its production station."))
        } else {
            job.complete(&self.inventory, &self.skill_catalog, &self.items)
        };
        match result {
            Ok(candidate) => {
                self.commit_inventory(candidate)?;
                self.pending
                    .push(self.items.get(job.recipe.product)?.packet());
                if let Some(window) = &mut self.workbench {
                    window.completed = true;
                    window.recovered_components = job.recovered_components.clone();
                    window.operation_failed = job.operation == CraftOperation::Analyze
                        && !job.analysis_succeeded
                        || job.operation == CraftOperation::Dismantle
                            && job.recovered_components.is_empty();
                    window.last_view = None;
                }
                let outcome = match job.operation {
                    CraftOperation::Manufacture => "Manufacturing completed",
                    CraftOperation::Analyze if job.analysis_succeeded => {
                        "Analysis succeeded; recipe learned"
                    }
                    CraftOperation::Analyze => "Analysis failed; sample consumed",
                    CraftOperation::Dismantle if job.recovered_components.is_empty() => {
                        "Dismantling failed; item consumed"
                    }
                    CraftOperation::Dismantle => "Dismantling completed; components recovered",
                };
                self.pending.push(packets::message(&format!(
                    "{outcome}: {}.",
                    self.items.get(job.recipe.product)?.name
                )));
                crate::server_log!(
                    "Production completed: {}; operation={:?}; product={}; analysis_succeeded={}",
                    self.account.character.name,
                    job.operation,
                    job.recipe.product,
                    job.analysis_succeeded
                );
            }
            Err(error) => {
                let mut candidate = self.inventory.clone();
                candidate.craft_job = None;
                self.commit_inventory(candidate)?;
                self.pending.push(packets::message(&format!(
                    "Production cancelled: {error}; no charge or materials consumed."
                )));
                crate::server_log!(
                    "Production cancelled: {}; reason={error}",
                    self.account.character.name
                );
            }
        }
        Ok(())
    }

    fn crafting_view(&self, view: &mut WorkbenchView) -> io::Result<()> {
        let window = self
            .workbench
            .as_ref()
            .ok_or_else(|| invalid("No production window."))?;
        view.cost = 0;
        view.base_cost = 0;
        view.products = self
            .inventory
            .known_formulas
            .iter()
            .copied()
            .filter(|id| {
                CraftRecipe::resolve(*id, &self.items).is_ok_and(|recipe| {
                    recipe.check_skill(&self.skill_catalog, &self.items).is_ok()
                }) && self
                    .items
                    .get(*id)
                    .is_ok_and(|item| view.filter & (1 << (item.level - 1)) != 0)
            })
            .collect();
        view.category = window.category;
        view.available_categories = view
            .products
            .iter()
            .filter_map(|id| self.items.get(*id).ok())
            .map(|item| (item.category, item.subcategory))
            .collect();
        if window.category != 0 {
            view.products.retain(|id| {
                self.items.get(*id).is_ok_and(|item| {
                    item.category == window.category as i32
                        || item.subcategory == window.category as i32
                })
            });
        }
        if matches!(window.mode, 2 | 3) {
            view.selected = view
                .target
                .as_ref()
                .map_or(window.selected, |sample| sample.template);
        }
        let Ok(recipe) = CraftRecipe::resolve(view.selected, &self.items) else {
            return Ok(());
        };
        view.cost = self.negotiated_service_price(recipe.cost)?;
        view.base_cost = recipe.cost;
        let mut available = BTreeMap::<u32, u32>::new();
        for item in self.inventory.cargo.iter().flatten() {
            *available.entry(item.template).or_default() += item.quantity;
        }
        view.components = recipe
            .components
            .iter()
            .map(|id| {
                let count = available.entry(*id).or_default();
                let present = *count > 0;
                *count = count.saturating_sub(1);
                (*id, u32::from(present))
            })
            .collect();
        if window.mode == 3 {
            let mut recovered = BTreeMap::<u32, u32>::new();
            for component in &window.recovered_components {
                *recovered.entry(*component).or_default() += 1;
            }
            view.components = recipe
                .components
                .iter()
                .map(|component| {
                    let count = recovered.entry(*component).or_default();
                    let present = !window.completed || *count > 0;
                    *count = count.saturating_sub(1);
                    (*component, u32::from(present))
                })
                .collect();
        }
        let result = if window.mode == 2 {
            view.chance = recipe
                .chance_with_equipment(&self.inventory, &self.skill_catalog, &self.items)
                .unwrap_or(0) as f32
                / 100.;
            recipe.analyze(&self.inventory, &self.skill_catalog, &self.items, true)
        } else if window.mode == 3 {
            view.chance = recipe
                .dismantle_chance_with_equipment(&self.inventory, &self.skill_catalog, &self.items)
                .unwrap_or(0) as f32
                / 100.;
            recipe.dismantle(
                &self.inventory,
                &self.skill_catalog,
                &self.items,
                &recipe.components,
            )
        } else {
            recipe.manufacture(
                &self.inventory,
                &self.skill_catalog,
                &self.items,
                &self.account.character.name,
            )
        };
        view.ready = result.is_ok();
        if window.completed {
            view.validity = if window.operation_failed { 14 } else { 16 };
        } else if self.inventory.craft_job.is_some() {
            view.validity = 0;
            view.failure = "Production in progress.".into();
        } else if let Err(error) = result {
            view.validity = if window.mode == 2
                && view
                    .target
                    .as_ref()
                    .is_some_and(|sample| !sample.builder_name.is_empty())
            {
                11
            } else {
                7
            };
            view.failure = error.to_string();
        } else {
            view.validity = 13;
        }
        Ok(())
    }

    pub(super) fn advance_production(&mut self, dt: f32) -> io::Result<()> {
        self.advance_crafting()?;
        if let Some(job) = self.inventory.refine_job.clone() {
            if job.station != self.sector.id {
                let mut candidate = self.inventory.clone();
                candidate.refine_job = None;
                self.commit_inventory(candidate)?;
                self.pending.push(packets::message(
                    "Refining cancelled because the ship left its station; no charge.",
                ));
            } else if unix_ms() >= job.due_ms {
                let catalog = RefineryCatalog::load()?;
                let result = catalog.get(job.product).and_then(|recipe| {
                    if job.recipe.as_ref() != Some(recipe) {
                        return Err(invalid("Refinery recipe changed; submit a new operation."));
                    }
                    recipe.apply(&self.inventory, job.iterations, job.unit_cost, &self.items)
                });
                match result {
                    Ok(mut candidate) => {
                        candidate.refine_job = None;
                        self.pending.push(self.items.get(job.product)?.packet());
                        self.commit_inventory(candidate)?;
                        if let Some(window) = &mut self.workbench {
                            window.completed = true;
                            window.last_view = None;
                        }
                        self.pending.push(packets::message(&format!(
                            "Refined {} {}.",
                            job.iterations,
                            self.items.get(job.product)?.name
                        )));
                        crate::server_log!(
                            "Refining completed: {}; product={}; iterations={}",
                            self.account.character.name,
                            job.product,
                            job.iterations
                        );
                    }
                    Err(error) => {
                        let mut candidate = self.inventory.clone();
                        candidate.refine_job = None;
                        self.commit_inventory(candidate)?;
                        self.pending.push(packets::message(&format!(
                            "Refining stopped: {error}; no materials or credits consumed."
                        )));
                        crate::server_log!(
                            "Refining stopped: {}; reason={error}",
                            self.account.character.name
                        );
                    }
                }
            }
        }
        self.workbench_sync -= dt;
        if self.workbench_sync <= 0. {
            self.workbench_sync = 0.2;
            self.refresh_workbench(false)?;
        }
        Ok(())
    }

    pub(super) fn refresh_workbench(&mut self, force: bool) -> io::Result<()> {
        if !self.logged_in || self.sector.station.is_none() {
            return Ok(());
        }
        let catalog = RefineryCatalog::load()?;
        let refine_price = self.negotiated_service_price(REFINE_PRICE)?;
        let mut view = WorkbenchView {
            mode: self.workbench.as_ref().map_or(0, |window| window.mode),
            selected: 0,
            ready: false,
            components: Vec::new(),
            validity: 0,
            failure: String::new(),
            additional: 0,
            filter: 0x1ff,
            products: Vec::new(),
            page: 0,
            total_products: 0,
            cost: refine_price,
            base_cost: crate::refining::REFINE_BASE_PRICE,
            chance: 1.,
            target: self.inventory.production_target[0].clone(),
            category: 0,
            available_categories: Vec::new(),
        };
        if let Some(window) = &self.workbench {
            view.selected = window.selected;
            view.filter = window.filter;
            if window.mode != 4 {
                self.crafting_view(&mut view)?;
            } else {
                view.products = catalog
                    .available(&self.inventory)
                    .into_iter()
                    .filter(|recipe| {
                        self.items
                            .get(recipe.output)
                            .is_ok_and(|item| window.filter & (1 << (item.level - 1)) != 0)
                    })
                    .map(|recipe| recipe.output)
                    .collect();
                if let Ok(recipe) = catalog.get(window.selected) {
                    let count = recipe.count_input(&self.inventory);
                    view.components = (0..recipe.input_quantity)
                        .map(|index| (recipe.input, u32::from(count > index)))
                        .collect();
                    let max =
                        recipe.max_iterations_for_cost(&self.inventory, &self.items, refine_price);
                    view.ready = max > 0;
                    view.additional = max.saturating_sub(1);
                    (view.validity, view.failure) = if window.completed {
                        (16, String::new())
                    } else if count < recipe.input_quantity {
                        (5, "Missing Component(s)".into())
                    } else if self.inventory.credits < refine_price {
                        (4, "Insufficient Credits".into())
                    } else if max == 0 {
                        (6, "Inventory Full".into())
                    } else {
                        (13, String::new())
                    };
                    if self.inventory.refine_job.is_some() {
                        view.validity = 0;
                        view.failure = "Refining in progress.".into();
                    }
                }
            }
            view.total_products = view.products.len() as u32;
            if !view.products.is_empty() {
                view.page = window.page.min(
                    (view.products.len() - 1).div_euclid(crate::production::FORMULA_PAGE_SIZE)
                        as u32,
                );
                view.products = view
                    .products
                    .into_iter()
                    .skip(view.page as usize * crate::production::FORMULA_PAGE_SIZE)
                    .take(crate::production::FORMULA_PAGE_SIZE)
                    .collect();
            }
            if !force && window.last_view.as_ref() == Some(&view) {
                return Ok(());
            }
        } else if !force {
            return Ok(());
        }
        for id in view
            .products
            .iter()
            .copied()
            .chain(view.components.iter().map(|(id, _)| *id))
            .chain((view.selected != 0).then_some(view.selected))
            .collect::<std::collections::BTreeSet<_>>()
        {
            self.pending.push(self.items.get(id)?.packet());
        }
        let previous = self
            .workbench
            .as_ref()
            .map_or(0, |window| window.known_count);
        self.pending.push(packets::workbench_snapshot(
            self.workbench_lab_id(),
            &view,
            &self.items,
        ));
        self.pending.push(packets::workbench_formulas(
            self.workbench_lab_id(),
            &view.products,
            previous,
            &self.items,
        ));
        self.pending
            .push(packets::workbench_page(self.workbench_lab_id(), &view));
        if let Some(window) = &mut self.workbench {
            window.known_count = view.products.len();
            window.page = view.page;
            window.last_view = Some(view);
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Paging exposes every learned formula beyond the native 500-slot limit without duplicates.
    #[test]
    fn manufacturing_pages_cover_large_skill_scoped_formula_sets() {
        let root =
            std::env::temp_dir().join(format!("enb-formula-pages-{}", rand::random::<u64>()));
        let mut account =
            crate::account::Account::create("tt".into(), "pass", "Trader".into()).unwrap();
        account.character.race = 0;
        account.character.profession = 1;
        account.save_new(&root).unwrap();
        let initial: Sector = serde_json::from_str(include_str!("../data/sector.json")).unwrap();
        let mut ship =
            PlayerSession::new(initial, Ipv4Addr::new(127, 0, 0, 2), root.clone()).unwrap();
        ship.sector = ship.sectors.sector(10761).unwrap();
        ship.logged_in = true;
        let expected = crate::recipe_grants::eligible_formulas(&ship.skill_catalog, &ship.items);
        assert!(expected.len() > crate::production::FORMULA_PAGE_SIZE);
        ship.inventory.known_formulas = expected.clone();
        ship.inventory.validate(&ship.items).unwrap();
        ship.workbench_input(
            0x79,
            &[ship.player_id().to_be_bytes(), 1u32.to_be_bytes()].concat(),
        )
        .unwrap();
        let mut seen = std::collections::BTreeSet::new();
        for page in 0..expected
            .len()
            .div_ceil(crate::production::FORMULA_PAGE_SIZE) as u32
        {
            ship.pending.clear();
            ship.workbench_page_input(
                &[ship.player_id().to_le_bytes(), page.to_le_bytes()].concat(),
            )
            .unwrap();
            let window = ship.workbench.as_ref().unwrap();
            let view = window.last_view.as_ref().unwrap();
            assert_eq!(view.page, page);
            assert_eq!(view.total_products as usize, expected.len());
            assert!(view.products.len() <= crate::production::FORMULA_PAGE_SIZE);
            assert!(window.valid(ship.sector.id));
            for id in &view.products {
                assert!(seen.insert(*id));
            }
            let metadata = ship
                .pending
                .iter()
                .find(|frame| frame.opcode == 0x2032)
                .unwrap();
            assert_eq!(
                metadata.body,
                [ship.workbench_lab_id(), page, expected.len() as u32, 500]
                    .map(u32::to_le_bytes)
                    .concat()
            );
        }
        assert_eq!(seen, expected);
        assert!(ship
            .workbench_page_input(
                &[ship.player_id().to_le_bytes(), u32::MAX.to_le_bytes()].concat()
            )
            .is_err());
        ship.workbench_input(
            0x7a,
            &[ship.player_id().to_be_bytes(), 110u32.to_be_bytes()].concat(),
        )
        .unwrap();
        assert_eq!(ship.workbench.as_ref().unwrap().page, 0);
        ship.workbench_input(
            0x80,
            &[
                ship.player_id().to_be_bytes().as_slice(),
                &[0],
                &0x1feu32.to_be_bytes(),
            ]
            .concat(),
        )
        .unwrap();
        let view = ship.workbench.as_ref().unwrap().last_view.as_ref().unwrap();
        assert_eq!(view.page, 0);
        assert!(view
            .products
            .iter()
            .all(|id| ship.items.get(*id).unwrap().level == 1));
        drop(ship);
        std::fs::remove_dir_all(root).unwrap();
    }

    /// A TT refinery job freezes the quoted discount before its durable completion.
    #[test]
    fn tradesman_refinery_persists_discounted_fee_and_charges_it_once() {
        let root =
            std::env::temp_dir().join(format!("enb-negotiate-refine-{}", rand::random::<u64>()));
        let mut account =
            crate::account::Account::create("tt".into(), "pass", "Trader".into()).unwrap();
        account.character.race = 0;
        account.character.profession = 1;
        account.save_new(&root).unwrap();
        let initial: Sector = serde_json::from_str(include_str!("../data/sector.json")).unwrap();
        let mut ship =
            PlayerSession::new(initial, Ipv4Addr::new(127, 0, 0, 2), root.clone()).unwrap();
        ship.sector = ship.sectors.sector(10761).unwrap();
        ship.logged_in = true;
        ship.inventory.cargo[0] = Some(crate::inventory::ItemInstance::stock(2845, 5));
        ship.inventory.credits = 121;
        ship.initialise_workbench_lab(true).unwrap();
        for (opcode, value) in [(0x79, 4u32), (0x7c, 3381)] {
            ship.workbench_input(
                opcode,
                &[ship.player_id().to_be_bytes(), value.to_be_bytes()].concat(),
            )
            .unwrap();
        }
        ship.inputs.push_back(1);
        ship.start_refining(false).unwrap();
        assert_eq!(ship.inventory.refine_job.as_ref().unwrap().unit_cost, 121);
        let saved: Inventory =
            serde_json::from_slice(&std::fs::read(ship.state_dir.join("inventory.json")).unwrap())
                .unwrap();
        saved.validate(&ship.items).unwrap();
        assert_eq!(saved.refine_job.as_ref().unwrap().unit_cost, 121);
        let mut due = ship.inventory.clone();
        due.refine_job.as_mut().unwrap().due_ms = 1;
        ship.commit_inventory(due).unwrap();
        ship.advance_production(0.1).unwrap();
        assert_eq!(ship.inventory.credits, 0);
        assert!(ship.inventory.refine_job.is_none());
        let completed = ship.inventory.clone();
        ship.advance_production(0.1).unwrap();
        assert_eq!(ship.inventory, completed);
        drop(ship);
        std::fs::remove_dir_all(root).unwrap();
    }
}
