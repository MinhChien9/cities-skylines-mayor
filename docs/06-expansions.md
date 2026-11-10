# CS1 Expansions — Complete Reference

This install owns **all 14 expansions** (verified from `gameSettings.cgs`, mask
`0x01A0FFFE`), so the agent may use all of them.

## Detecting what is available

```csharp
// The ONLY reliable runtime check.
bool has = managers.application.SupportsExpansion(Expansion.Industry);
```

**Never** probe the filesystem for expansion assets — **all expansion prefabs ship in the
base install** regardless of ownership. Only `m_supportsExpansion[i]` gates them.

### The `Expansion` enum **[C]** — verified from `ICities.dll`

```csharp
public enum Expansion {
    None = -1, AfterDark, Snowfall, NaturalDisasters, InMotion, GreenCities,
    Parks, Industry, Campuses, Urban, Airport, PlazasAndPromenades,
    FinanceExpansion, Hotels, RacesAndParades
}
```

> ⚠️ **Do not pass `Expansion.None` (-1)** — `SupportsExpansion` indexes
> `m_supportsExpansion[(int)expansion]` and would go out of bounds.

### Naming traps **[C]**

| Common assumption | Reality in CS1 |
|---|---|
| `Expansion.Campus` | **`Expansion.Campuses`** (plural) |
| `Expansion.Urban` = tourism | `Expansion.Urban` = **Sunset Harbor** (Fishing Industry) |
| "Monetary Policy" expansion | **CS2 only.** CS1 has `FinanceExpansion` = **Financial Districts** |
| "Tourism" expansion | **Tourism is base game** in CS1, enhanced by After Dark / Parks / Campus / Hotels |
| "Carbon footprint" policies | **CS2 concept.** Not in CS1 |

Resolution chain **[C]**: `SupportsExpansion` → `LoadingManager.m_supportsExpansion[i]` →
`SteamHelper.IsDLCOwned(id)` → `EpicSDK.instance.IsDLCOwned(dlc)` → live Epic catalogue.
Ownership is resolved **at runtime from the Epic catalogue**, not from any local file.

---

## Industry (DLC 715194)

### The chain

```
raw resource → processor → Special Good → factory → generic goods → commercial
```

4 raw types (agricultural, forestry, ore, oil) → **8 Special Goods** (2 per industry) →
**Luxury Goods** from Unique Factories.

> **Special Goods cannot be imported.** Only Crops can be imported into a factory.
> If no matching local supply exists, generic industry **imports** rather than produces.

### Building types

Extractors (on the resource) · Processors · Factories · Auxiliary · Warehouses.

Each Industry Area level = **+4% production, −10% pollution**.

**Area level thresholds [W]:** L1 = place Main Building · L2 = 500 res / 150 workers ·
L3 = 1,500 / 350 · L4 = 4,500 / 550 · L5 = 13,500 / 800.

### Pollution per extractor **[W]**

| Building | Pollution | Noise |
|---|---|---|
| Small Tree Plantation (L1) | 0 | 0 |
| Large Tree Plantation (L5) | 0 | 0 |
| Biomass Pellet Plant (L2) | 25 | 50 |
| Pulp Mill (L5) | 30 | 70 |
| Sawmill (L1) | 0 | 50 |
| Engineered Wood Plant (L4) | 0 | 50 |

> **Forestry = no ground pollution but high noise.** Fertile land = renewable, no ground
> pollution, increased water demand. Oil/Ore = depletable and polluting.

### Unique Factories (one of each per city) **[W]**

| Factory | Cost | Pollution | Noise | Jobs |
|---|---|---|---|---|
| Furniture Factory | 22,000 | 15 | 15 | 70 |
| Bakery | 20,000 | 5 | 10 | 80 |
| Industrial Steel Plant | 75,000 | 25 | 50 | 150 |
| Household Plastic | 50,000 | 20 | 20 | 80 |
| Toy Factory | 100,000 | 20 | 25 | 140 |
| Printing Press | 52,000 | 20 | 20 | 90 |
| Lemonade Factory | 50,000 | 5 | 10 | 130 |
| Electronics Factory | 150,000 | 20 | 20 | 180 |
| Clothing Factory | 100,000 | 15 | 20 | 150 |
| Petroleum Refinery | 175,000 | 70 | 70 | 190 |
| Soft Paper | 100,000 | 30 | 50 | 200 |
| Car Factory | 300,000 | 25 | 25 | 260 |
| Food Factory | 200,000 | 20 | 25 | 250 |
| Sneaker Factory | 250,000 | 20 | 20 | 200 |
| Modular House Factory | 180,000 | 20 | 30 | 170 |
| Shipyard | — | — | — | — |
| Seafood Factory | (Industries + Sunset Harbor) | | | |

### Exports **[W]** — 4 routes: road (outside highway), rail (cargo terminals), ship (cargo
harbors), cargo planes (cargo airports).

> **Schools of Engineering** (a Trade School unique faculty) increase the income of **all**
> generic zoned industry.

### Zoned industry specializations (base game)

Farming & Forestry at **Worthy Village**, Ore at **Boom Town**, Oil at **Busy Town**.
Specialized zoned buildings have a **single level** (no level-up).

---

## Campus (DLC 944071)

### Structure

3 campus types — **Trade School**, **Liberal Arts College**, **University**. Each needs an
**Administration Building** (unique, one per city) placed first; it defines the type.
22 buildings per type: Administration (1) + Education (5 + faculties) + Supplementary (13).

### Reputation levels **[W]** — campuses **CAN level down**

| Level | Academic Works | Students | Attractiveness |
|---|---|---|---|
| 2 Recognized | 2 | 500 | 200 |
| 3 Renowned | 4 | 800 | 450 |
| 4 Acclaimed | 8 | 1,200 | 900 |
| 5 Prestigious | 16 | 1,800 | 1,800 |

### Academic works **[C]/[W]**

- Created at the **end of each academic year**.
- Chance = 1 work per **500 students**, max **4** per year; base chance **30%**.
- Each academic-staff building adds **+5%** chance.
- 3 categories per campus, up to **48** works each (144 total): Artisanry, Hospitality,
  Literature, Cinematography, Discovery, Dissertation, IT and Engineering, Music,
  Scientific Study.
- Research grant: **50,000** → 1 guaranteed extra work in a chosen category.
- Academic staff: 5 free, up to 20 more at **1,000** each.

> **Students are a campus-local population** — they are *not* part of the city's
> residential population. Exchange students count as **tourists**.

### Varsity and museums

Aquatics Center, Basketball Arena, Track and Field Stadium, Baseball Park, American
Football Stadium (3 policies). Museums: Technology Museum, Art Gallery, Science Center.

---

## Sunset Harbor / `Expansion.Urban` (DLC 1146930)

**The Fishing Industry** — not a tourism pack.

4 fish types by water depth and flow: **Anchovies** (still, shallow) · **Salmon** (shallow
+ flow) · **Shellfish** (deep + still) · **Tuna** (deep + flow).

Buildings: Fishing Harbor + 4 variety harbors, Fish Farm, Algae Farm, Seaweed Farm,
Fish Market, Fish Factory.

**Unlocks:** Fishing Industry + Harbor + Market at **Tiny Town** · Fish Factory at
**Boom Town** · Fish Farm at **Busy Town**; the rest by production quantity.

**Production gates [W]:** Harbor 02/03/04/05 at 250,000 / 2,000,000 / 2,000,000 / 250,000 ·
Fish Farm 02 at 800,000, 03 at 3,200,000 · **Seafood Factory requires BOTH Industries and
Sunset Harbor** and 500,000 combined production.

Also ships: Intercity Bus + hub, Trolleybus + depot, Helicopter Depot/Stop + Aviation
Club, Water Treatment Plant, Waste Transfer Facility, Large Water Tower, Cargo Harbor,
Cargo Hub, Metro/Intercity/Train/Bus hubs, Metropolitan Airport.

### Tourism (base game, not this expansion)

Tourists arrive by highway (start), ship, plane, train, intercity bus, or their own car.
3 wealth tiers — richer tourists stay longer. Attractiveness is driven by citizen
happiness, land value and unique buildings.

**Tourism specialization** unlocks at **Busy Town**: more tax income, higher crime, active
during the day, single-level, jobs = level-2 high-density commercial.
**Leisure specialization** unlocks at **Big Town**: more income, more noise, 24h traffic,
single-level, jobs = level-3 low-density commercial.

**Unique-building tourism effects [W]:** City Arch +5% road tourist capacity · Sea Fortress
+5% ship/airport capacity · Observation Tower +10% park radius · Statue of Colossalus +20%
tourist stay · Old Market Street +5% nearby commercial tax · Clock Tower +20% train speed.

---

## Airports (DLC 1726380)

### Airport Area

Painted with the area brush (levels terrain, removes trees). **The only area brush with a
soil cost.** Unlocks at **Boom Town** — earlier than the ploppable airport buildings.

### Levels 1→3

Leveling by **passengers transported + airport attractiveness**. No level-down, except
removing the initial terminal resets progression. **Cargo assets unlock at level 3**; a
cargo-only area **stays level 1**.

### Required structure

terminal + plane stand connected to terminal/concourse + runway + taxiway network.

**Runways:** one-way, **minimum length 50 units**, no maximum, cannot cross or connect
with roads or each other. **Taxiways connect at only 4 points** (2 near each end — one
exit, one entrance).

**Aircraft stands:** small/medium/large passenger (must connect to terminal/concourse);
cargo stands are one size and may sit on normal roads or Cargo Airport Roads.

**Concourse costs [W]:** Classic 90/cell (1.44 upkeep/wk) · Modern 110 (1.76) ·
Ultra Modern 130 (2.08).

**Terminal styles:** Classic < Modern < Ultra Modern, each with small one-story, small
two-story (split arrivals/departures), and large two-story with **built-in underground
metro station** (unlocks at level 2).

**Cargo Airport Roads:** Medium = 4 lanes with utility strip; Large = 6 lanes
(2 regular + 2 contraflow + 2 loading/unloading).

A **Cargo Airport Terminal** is optional if a passenger terminal exists, mandatory for a
cargo-only area, and only cargo trucks may pass through it.

**City Airline:** ticket prices, 6 airline designs, custom names. Aircraft: 2 large,
3 medium, 3 small passenger + 2 cargo.

---

## Plazas & Promenades (DLC 2008400)

### Pedestrian Areas + Service Points

Pedestrians walk freely on pedestrian-only streets. **Service points are required** for
cargo and garbage.

| Service point | Cost | Upkeep/wk | Cargo | Garbage | Unlock |
|---|---|---|---|---|---|
| Small Pedestrian Area SP | 16,000 | 256 | 5 | 3 | create a pedestrian area |
| Large SP | 45,000 | 720 | 15 | 10 | 12 cargo trucks/week |
| Small Cargo SP | 14,000 | 224 | 7 | — | 5 trucks/wk |
| Large Cargo SP | 54,000 | 864 | 30 | — | 15 trucks/wk |
| Small Garbage SP | 10,000 | 160 | — | 5 | 3 trucks/wk |
| Large Garbage SP | 36,000 | 576 | — | 20 | 9 trucks/wk |

### Plazas

Placed anywhere; visitor capacity matters.

| Plaza | Cost | Upkeep | Ent | Vis | Unlock |
|---|---|---|---|---|---|
| Small Food Truck | 800 | 13 | 50 | 75 | — |
| Small Ice Cream | 800 | 13 | 50 | 75 | — |
| Small Fountain | 1,000 | 16 | 50 | 105 | — |
| Small Glass Roof | 4,000 | 64 | 100 | 210 | across road |
| Statue Plaza | 2,000 | 32 | 75 | 120 | — |
| Large Food Truck | 1,500 | 24 | 75 | 90 | — |
| Large Ice Cream | 1,500 | 24 | 75 | 90 | — |
| Flower Plaza | 2,500 | 40 | 75 | 105 | pedestrian area, land value ≥ 300/cell |
| Large Fountain Plaza | — | — | — | — | land value ≥ 380 |
| Large PedArea Plaza | — | — | — | — | land value ≥ 450 |

**3 new wall-to-wall specializations:** offices, high-density residential, high-density
commercial — 24 new modern wall-to-wall buildings each.

**3 pedestrian-area policies:** Slow Driving (20 mph everywhere except highways),
Sugar Ban (↑lifespan, ↓happiness), Street Music (↑happiness, ↑noise).

---

## Financial Districts (DLC 2148901)

### Stock Exchange (unique, 12×8, 5 levels) **[W]**

| Level | Gains required | Upgrade cost | Upkeep/wk | Tax bonus |
|---|---|---|---|---|
| 1 | 0 | 60,000 | 960 | +2% |
| 2 | 50,000 | 15,000 | 1,280 | +3% |
| 3 | 250,000 | 30,000 | 1,600 | +4% |
| 4 | 750,000 | 60,000 | 1,920 | +5% |
| 5 | 1,500,000 | 120,000 | 2,240 | +6% |

Unlocks Small Bank → Large Bank → Skyscraper Bank, Bronze Cow/Panda, Elevated Plaza,
Underground Garden Plaza, International Trade Building.

### Investments **[C]**

Invest city money in sectors: **deathcare, healthcare, oil, ore, farming, generic industry,
logistics** — tracked in the stock trading tab.

**Bank buildings** are a new service: their transport vans collect cash from commercial
buildings and **reduce criminality in the area**.

**Financial Offices** office specialization unlocks at **Big Town**; higher taxes than basic
offices but lower happiness; tax bonus rises near a Stock Exchange or International Trade
Building.

---

## Hotels & Retreats (DLC 2342310)

15 hotels + City Hotel (4 variants), 3 per star tier, unlocked 3 at a time.

| Tier | Hotel | Cost | Upkeep/wk | Guest cap | Size |
|---|---|---|---|---|---|
| L1 | Budget Hotel | 40,000 | 672 | 70 | 5×3 |
| L1 | Rental Cabin | 10,000 | 128 | 5 | 4×4 |
| L1 | Town Hostel | 25,000 | 120 | 50 | 2×3 |
| L2 | Small Hotel | 80,000 | 1,280 | 90 | 4×6 |
| L2 | Old Inn | 50,000 | 960 | 40 | 5×6 |
| L2 | Roadside Motel | 35,000 | 320 | 50 | 9×4 |
| L3 | City Hotel | 120,000 | 2,880 | 200 | 5×8 |
| L3 | Mountain Lodge | 140,000 | 3,200 | 120 | 11×6 |
| L3 | Boat Hotel | 60,000 | 1,040 | 85 | 18×21 (shoreline only) |
| L4 | Conference Hotel | 200,000 | 6,560 | 350 | 12×8 |
| L4 | Spa Resort | 150,000 | 3,520 | 170 | 13×16 |
| L4 | Ocean Resort | 170,000 | 3,840 | 70 | 16×29 (shoreline only) |
| L5 | Heritage Hotel | 300,000 | 8,960 | 450 | 14×11 |
| L5 | Deluxe Hotel | 270,000 | 8,160 | 650 | 22×13 |
| L5 | Design Hotel | — | — | — | — |

**Milestone gates [W]** — each tier needs profit **and** average popularity:

| Tier | Profit | Popularity |
|---|---|---|
| 2★ | ≥ 1,000 | ≥ 60 |
| 3★ | ≥ 5,000 | ≥ 70 |
| 4★ | ≥ 15,000 | ≥ 80 |
| 5★ | ≥ 40,000 | ≥ 90 |

**Hotel focus flags [C]:** Sightseeing(1) / Shopping(2) / Business(4) / Nature(8), each
0–100 attractiveness, default 25. `m_maxNewGuestsPerDay` default 3.

**9 hotel events** with tourism bonuses (e.g. Press Conference +30%, Award Ceremony +30%,
Travel Fair +30%).

---

## Parklife (DLC 715191)

4 park types via **Main Gate**: Amusement Park, City Park, Nature Reserve, Zoo — each with
a regular and a Small Main Gate. **Only one main gate per Park Area** (extras are Side
Gates). The main gate connects the park to the road network and sells tickets.

**Levels 1–5** by entertainment accumulation + visitor count **[W]**:

| Level | Amusement/Nature/Zoo (ent/vis) | City Park (ent/vis) |
|---|---|---|
| 2 | 250 / 500 | 220 / 500 |
| 3 | 500 / 2,500 | 420 / 2,500 |
| 4 | 1,000 / 5,000 | 720 / 5,000 |
| 5 | 2,000 / 10,000 | 1,120 / 10,000 |

**Parks do not level down** (campus and industry areas do).

**Park Maintenance Depot** boosts happiness and park effectiveness; budget 50–150%.
Tours: walking and sightseeing bus, with adjustable ticket prices.

---

## After Dark (DLC 369150)

Night-time economy: citizens flock to nightclubs/karaoke bars after work; leisure is
especially active at night and drives land value.

- **Leisure specialization** at **Big Town**: nightclubs, bowling alleys, arcades — more
  income, more noise, 24h traffic, single-level, jobs = level-3 low-density commercial.
- **Tourism specialization** at **Busy Town**: hotels, restaurants, souvenirs — more tax
  income, higher crime, daytime, jobs = level-2 high-density commercial.

New services: **Prison**, **Cargo Hub**, **International Airport**, **Bus Terminal**.
New transport: bike lanes/ways, bus lanes, **Taxi** (for citizens *and* tourists).

---

## Snowfall (DLC 420610)

Cold weather **increases heating (energy) demand**. Heating pipes; buildings consume
electricity for heat unless a water-based heat system (Boiler Station, Geothermal plant) is
used.

Snow accumulation + **Snow Dump** and snow plough depot keep traffic flowing; **Road
Maintenance Depot** keeps streets in shape and increases speed on icy roads.

Policies: Anti-Slip Studs (₡4/senior citizen), Combustion Engine Ban, Studded Tires,
Winter Maintenance. New transport: **Trams** running on existing roads.

> If Snowfall is **not** owned, the game forces the environment from "Winter" to "Sunny".

---

## Natural Disasters (DLC 515191)

9 disasters: **Tsunami, Tornado, Thunderstorm, Earthquake, Sinkholes, Forest Fire, Meteor
Strike**, plus vanilla **Collapse** and **Fire**.

Warning lead time depends on the **Emergency Services** budget. One Weather Radar covers
the whole city; Earthquake Sensors have a radius that scales with budget (so you may need
several).

**Mitigation:** Disaster Response Unit (searches collapsed buildings), Small/Large
**Emergency Shelter** + escape routes, helicopters (Fire, Medical, Police, Disaster
Response), Lightning Rods policy.

**Fire Helicopter Depot:** each depot supports **3 helicopters**, which must dip buckets in
a river/lake/sea to refill before reaching the fire.

Buildings: Tsunami Warning Buoy, Weather Radar, Earthquake Sensor, Deep Space Radar, Fire
Watch Tower, Fire/Medical/Police Helicopter Depot, Disaster Response Unit, Emergency
Shelters, Disaster Memorial, Doomsday Vault, Pyramid of Safety, Space Radar.

---

## Green Cities (DLC 614580)

350 new assets: eco-friendly buildings, organic/local produce commercial specialization,
Electric Car Factory, alternative service buildings, new unique buildings, new parks.

**Specializations can now level up** for the first time in CS1.

Policies: Boost Connections, Combustion Engine Ban, Electric Cars, Encouraging Biking,
Filter Industrial Waste, Heavy Traffic Ban, NIMBY, No Rebuilding, Old Town, Workers' Union,
Airplane Tours, Automated Toll, Big Business Benefactor, Small Business Enthusiast, High
Tech Housing, Industrial Space Planning, Highrise Ban, Education Boost, For-Profit
Education, Student Healthcare, Universal Education, Visiting Scholars, Advertisement
Campaign (Varsity), Tourism policies.

> **Carbon footprint / carbon policies do not exist in CS1** — that is a CS2 concept.

---

## Race Day / `Expansion.RacesAndParades` (DLC 4031130)

Racing circuits and parades. Milestone strings present in the assets:
`Racing HQ Created`, `Event Park Created`.

---

## Unlocking: the pipeline step people forget

> **CS1 loads asset bundles based on unlocks AT CITY-LOAD TIME.**
> `PrefabCollection<BuildingInfo>.FindLoaded` only returns *loaded* prefabs.

So the correct sequence is:

```
unlock  →  save  →  reload the city  →  then the new content is placeable
```

Calling `UnlockManager.UnlockAllProgressionMilestones()` mid-session will **not**
retroactively load the bundles.

Two more caveats:

1. Some prefabs load but do not work. The upstream project ships a blocklist
   (`"Block Services - …"`). Keep that policy.
2. **Milestone unlocking is a cheat** that invalidates economic evaluation, because
   milestones drive demand. Decide explicitly whether the agent may cheat, and say so in
   every evaluation.

## Agent guidance per expansion

| Expansion | What the agent should do with it |
|---|---|
| **Industry** | Zone specialized industry areas, chain raw→processed→goods, give cargo routes first |
| **Campus** | Place Administration first, then staff to generate academic works, keep reputation up |
| **Sunset Harbor** | Place fishing harbors on fish areas, chain to fish farms and the seafood factory |
| **Airports** | Paint the airport area, build terminal + stands + runway + taxiways, then set an airline |
| **Plazas** | Use pedestrian areas in dense cores, service points for cargo/garbage, plazas for land value |
| **Financial Districts** | Build a Stock Exchange, invest in sectors, use banks for crime reduction |
| **Hotels** | Match hotel tier to tourism demand, set focus flags, monitor profit + popularity milestones |
| **Parks** | Place a Main Gate, fill the area, keep the Park Maintenance Depot funded |
| **After Dark** | Place leisure away from residential, use taxi + intercity bus for tourists |
| **Snowfall** | Add heating, keep the Snow Dump and Road Maintenance Depot funded |
| **Natural Disasters** | Place shelters and sensors, budget Emergency Services |
| **Green Cities** | Use Electric Cars and Filter Industrial Waste, plant trees to shield noise |
| **Race Day** | Place the Racing HQ and event parks |
