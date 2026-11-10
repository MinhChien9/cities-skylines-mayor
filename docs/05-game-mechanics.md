# CS1 Game Mechanics — Knowledge Base

Everything here was verified against the actual game assemblies
(`D:\Epic Games\CitiesSkylines\Cities_Data\Managed\Assembly-CSharp.dll`, decompiled with
ILSpy) or against the official wiki. Numbers marked **[C]** come from the code;
numbers marked **[W]** come from the wiki.

This is the reference the agent should reason from. It is deliberately numeric.

---

## 1. Demand model (RCI)

### Formulas **[C]** — `ZoneManager.CalculateResidentialDemand` etc.

```
Residential:
  jobs      = commercial.finalHomeOrWork + industrial.finalHomeOrWork
            + office.finalHomeOrWork + player.finalHomeOrWork
  vacant   = commercial.finalEmpty + industrial.finalEmpty + office.finalEmpty + player.finalEmpty
  resOcc   = residential.finalHomeOrWorkCount
  resEmpty = residential.finalEmptyCount
  unemployed = Σ educated0..3.finalUnemployed
  homeless   = Σ educated0..3.finalHomeless

  target = Clamp(100 - resOcc, 50, 100)
  target += Clamp((vacant*200   - unemployed*200) / Max(jobs,100), -50, +50)
  target += Clamp((homeless*200 - resEmpty*200) / Max(resOcc,100), -50, +50)
  target  = Clamp(target, 0, 100)
```

```
Commercial:
  comJobs  = commercial.finalHomeOrWork - commercial.finalEmpty
  workers  = residential.finalHomeOrWork - residential.finalEmpty
  visitors = visitor.finalHomeOrWork ; visitorEmpty = visitor.finalEmpty

  t = Clamp(workers, 0, 50)
  j = comJobs * 10 * 16 / 100      // comJobs * 1.6
  w = workers * 20 / 100           // workers * 0.2
  t += Clamp((w*200 - j*200) / Max(j,100), -50, +50)
  t += Clamp((visitors*100 - visitorEmpty*300) / Max(visitors,100), -50, +50)
  t  = Clamp(t, 0, 100)
```

```
Workplace (industrial + office):
  workers = residential.finalHomeOrWork - residential.finalEmpty
  jobs    = com + ind + office + player finalHomeOrWork
  vacant  = com + ind + office + player finalEmpty
  unemployed = Σ educated0..3.finalUnemployed

  t = Clamp(workers, 0, 50)
  t += Clamp((unemployed*200 - vacant*200) / Max(jobs,100), -50, +50)
  t = Clamp(t, 0, 100)
```

### Interpretation

- **R never drops below 50** while housing exists (`Clamp(100-resOcc, 50, 100)`).
- **C = 0 at 1 commercial worker per 8 citizens** — the `w` term cancels `j` at that ratio. **[W]**
- **I ≈ unemployment − unfilled vacancies**, capped at 50 base + 50 bonus.
  If I ≈ 0 while unemployment is high, you are job-rich — zone commercial/office, not industry.

### Smoothing **[C]** — `ZoneManager.UpdateDemand`

```
if (target > demand) demand = Min(demand + 2, target);
else if (target < demand) demand = Max(demand - 2, target);
```

Two-stage: raw target → `m_actualResidentialDemand` (±2/step) → displayed demand
(±2/step again, plus the district tax offset).

> **Demand moves ±2 per simulation step.** A 50-point swing takes ~25 steps. Never expect
> a demand bar to react instantly.

### Start-of-map values **[C]**: R = C = I = **50**. That is a seed, not an equilibrium.

---

## 2. Taxes

### Rates

- Stored per `(Service, SubService, Level)` in `EconomyManager.m_taxRates`. Default **9%**. **[C]**
- UI range 1–29% **[W]**; internal range 0–100.
- Taxation policies (`TaxRaise*` / `TaxLower*`) shift by exactly **±2 points**. **[C]**
- `DontTaxLeisure` ⇒ **0%** on leisure. **[C]**

### The tax ceiling — `District.AddTaxEffect` **[C]**

```
taxRate = EconomyManager.GetTaxRate(service, subService, level, districtTaxPolicies)
if (taxRate > limit) result -= (taxRate - limit) * count
divider += count
offset = (result * 5 + divider/2) / divider
```

| Zone / level | limit (%) |
|---|---|
| Residential **Low** L1–L5 | 11 / 10 / 10 / 9 / 9 (by building wealth) |
| Residential **High** L1–L5 | 12 / 11 / 10 |
| Commercial Low L1/L2/L3 | 11 / 10 / 9 |
| Commercial High L1/L2/L3 | 12 / 11 / 10 |
| Industrial L1/L2/L3 | 11 / 10 / 9 |
| Office L1/L2/L3 | 11 / 10 / 9 |

> **Every tax point above the limit costs 5 demand points**, weighted by the mix of
> buildings of that class. **Below the limit there is NO demand bonus.**
>
> **Keep tax at 9%** early — 9% is at or under the limit for every zone type at every
> level. Only raise above 11–12% when desperate.

### The global tax multiplier **[C]**

```
num17 = (lastCashDelta - 200000) * 20000 / num15
num18 = num17 > 0  ? 5000 + 50000000/(num17+10000)
      : num17 >= 0 ? 10000
                    : 20000 + 200000000/(num17-20000)
num18 = num18 * 1e9 / (1e9 + max(cash,0))          // shrinks with treasury
num18 = num18 * 1e6 / (1e6 + max(population,0))    // shrinks with population
num18 = Clamp(num18, 100, 30000)                    // 1% .. 300%
num18 = (m_taxMultiplier * 99 + num18) / 100        // damped 99/1
```

Base `m_taxMultiplier = 10000` (= 100%). Surplus ⇒ multiplier **below** 100% ⇒ *less* tax
income the richer and bigger the city is. Deficit ⇒ above 100% (self-correcting). **[W]**

### Income sources **[C]**

| Source | Resource |
|---|---|
| Residential / commercial / industrial / office buildings | `PrivateIncome` |
| Commercial | split into `CitizenIncome` + `TourismIncome` by tourist ratio |
| Concerts, sports, events | `PublicIncome` |
| Taxis, hotels, cargo trucks, cars, aircraft | `PublicIncome` |
| District parks | `PublicIncome` |

---

## 3. Service budgets **[C]**

`m_serviceBudgetDay[]` / `m_serviceBudgetNight[]`, default 100/100.

```
budget < 100        → b = (budget² + 99) / 100
budget > 150        → b = 125
100 < budget ≤ 150  → b = budget - (100-budget)²/100
```

- **50% ⇒ 25% effect. 150% ⇒ 125%.**
- The budget scales the service **radius** as well as its strength, so a starved police
  force both loses coverage *and* blocks industrial/office level-ups.
- **101% ⇒ 101% effect ⇒ +1 vehicle per building** at almost no cost (garbage truck,
  ambulance, hearse, fire truck, police car, road maintenance). **[W]**
- Day = 05:00–20:00 (`SUNRISE_HOUR=5`, `SUNSET_HOUR=20`).

---

## 4. Coverage **[C]** — `CoverageManager`

- Coverage propagates **along the road network**, not radially.
- Node coverage byte = `Clamp(Round(dist * 254 / fadeLength), 0, 254)`; **255 = no coverage**.
- `dist = travelTime − currentRange + fadeLength/2`, where travel time is road distance /
  average lane speed (or distance / 0.5 for pedestrian paths).
- `currentRange = productionRate × baseRadius × 0.01`, where `productionRate` is scaled by
  the budget and forced to 0 when the building is inactive or short of power/water.

> **A service across an unconnected block covers nothing.** Ensure every zoned block is
> road-connected to its service.

### 0% coverage consequences **[W]**

| Service | Effect |
|---|---|
| Power / water / sewage | buildings stop working → `RateReduced` (≤50% production) → abandonment |
| Garbage | uncollected garbage → ground pollution → sickness → abandonment |
| Fire | buildings burn down; long response time → unhappiness |
| Police | crime rises → land value down → abandonment |
| Health / death care | untreated illness → death |
| Education | uneducated workers → industry/office level-up blocked |

---

## 5. Zoning and building levels

### Zone types **[C]**

`Unzoned=0, Distant=1, ResidentialLow=2, ResidentialHigh=3, CommercialLow=4,
CommercialHigh=5, Industrial=6, Office=7, None=15`

### Capacity **[W]**

| Zone | Households/jobs per cell by level |
|---|---|
| Residential Low | 0.20 / 0.25 / 0.33 / 0.375 / 0.417 |
| Residential High | 0.6 / 1.0 / 1.33 / 1.5 / ~1.6 |
| Commercial Low | 0.5 / 0.75 / 1.0 jobs |
| Commercial High | 0.75 / 1.0 / 1.25 jobs |
| Industrial | 1.0 / 1.5 / 2.0 jobs |
| Office | 0.5 / 1.1 / 1.75 jobs |

**High-density residential holds ~4× the households of low density in the same area.**

### Level-up drivers **[W]**

| Zone | Levelled by |
|---|---|
| Residential | **land value** + **average education of residents** |
| Commercial | **land value** + **average wealth of shoppers** (home level) |
| Industrial | **average education of workers** + **service coverage score** |
| Office | **average education of workers** + **service coverage score** |

> Services do **not** directly raise residential/commercial level — only indirectly via
> land value. If a residential block refuses to level up, fix **schools** first, then
> **land value**.

### Service-coverage score weights **[W]**

| Service | Industrial | Office |
|---|---|---|
| Cargo transport | **+1.0** | 0 |
| Fire | +0.5 | +0.2 |
| Public transport | +0.333 | +0.333 |
| Police | +0.2 | +0.2 |
| Health care | +0.2 | +0.2 |
| Death care | +0.2 | +0.2 |
| Entertainment | +0.125 | +0.167 |
| Elementary / High school / University | +0.125 each | +0.143 each |
| Noise pollution | −0.143 | −0.25 |
| Abandonment | −0.143 | −0.333 |
| Ground pollution | −0.167 | −0.25 |

**Cargo transport is worth 5× police and 2× fire.** Give every industrial block a cargo
route before adding more fire/police.

### Pollution per building level **[C]** — `GetPollutionRates`

| Building | Ground | Noise |
|---|---|---|
| Residential | 0 | 0 |
| Commercial Low generic | — | 140 / 120 / 100 |
| Commercial High generic | — | 200 / 175 / 150 |
| Industrial generic | 300 / 200 / 150 | 300 / 200 / 150 |
| Industrial Ore | 400 | 500 |
| Industrial Oil | 500 | — |
| Farming / Forestry | 0 | — |

A level-3 industry emits half of level-1. Cleanup matters as much as zoning.

### Zoning mechanics **[C]**

- Zone blocks are created **by roads** (`RoadAI.CreateZoneBlocks` → `NetSegment.UpdateZones`).
  **You cannot zone empty land — build the road first.**
- Zoning depth: **4 cells** from a road. Highways do **not** allow roadside zoning.
- `ZoneBlock` is a **struct** — after mutating it you MUST write it back to
  `m_blocks.m_buffer[id]`. Missing this is a classic silent no-op bug.
- Cell bit layout: `index = (z << 3) | ((x & 1) << 2)`, 4 bits per cell.

---

## 6. Roads and transport **[W]**

| Road | Cost | Upkeep/wk/cell | Speed |
|---|---|---|---|
| Four-Lane Road | 60 | 0.80 | 50 |
| Four-Lane + Bus Lanes | 60 | 0.80 | 50 |
| Large Avenue | 80 | 0.60 | 50 |
| Three-Lane Highway | 70 | 0.96 | **100** |
| Four-Lane Highway | 110 | 1.76 | **100** |
| Highway Ramp | 30 | 0.32 | 80 |

Elevated ≈ 2–3× upkeep; underground highway ≈ 5.3×.

| Transport | Capacity |
|---|---|
| Bus | 30 (variants 20–100) |
| Tram | 90 |
| Metro | 150 |
| Monorail | 180 |
| Train | 240 |
| Ship | 100 |
| Plane | 200 |

A bus costs **₡12/week regardless of distance** — distance, not passenger count, drives fleet size.

**Highways and ramps block roadside zoning.** Always pair a highway with parallel
low-density streets.

---

## 7. Happiness, health, crime **[C]** — `Citizen`

```
GetHappiness(health, wellbeing) = (health + wellbeing + 1) >> 1
```

**Health levels:** ≤10 VerySick · ≤25 Sick · ≤50 PoorHealth · ≤60 Healthy · ≤80 VeryHealthy · else ExcellentHealth.

**Happiness bands:** ≤15 Bad · ≤30 Poor · ≤44 Good · ≤69 Excellent · >69 Suberb.

**Wellbeing thresholds by education** (VeryHappy / Happy / Satisfied / Unhappy):

| Education | thresholds |
|---|---|
| Uneducated | 60 / 35 / 25 / 10 |
| One school | 70 / 45 / 31 / 16 |
| Two schools | 80 / 60 / 40 / 20 |
| Three schools | 85 / 63 / 50 / 26 |

> Educated citizens are both happier **and** more employable.

**Crime:**
```
base by unemployment length: 0→10, 1→15, 2→20, 3→25, 4→35, ≥5→50
max by wellbeing: VeryUnhappy 100, Unhappy 85, Satisfied 70, Happy 55, VeryHappy 40
normal:   crime = min(base, max) >> 1
criminal: crime = min(base<<1, max) << 1
```

**Health-care need per age phase:** Child 150 · Teen 30 · Young 60 · Adult 100–125 · Senior 200.
**Death-care need:** Child/Teen 0 · Young 10–20 · Adult 50–80 · Senior 100–160.

**CS1 has no CS2-style "approval rating" panel.** The analogue is average happiness
(`District.m_finalHappiness`).

---

## 8. Milestones **[W]**

| # | Milestone | Population (min–max) | Areas | Key unlocks |
|---|---|---|---|---|
| 0 | Start | — | 1 | Power, water, roads; Residential/Commercial/Industry zones |
| 1 | Little Hamlet | 120–500 | 1 | Loans, taxes; education, garbage, healthcare |
| 2 | Worthy Village | 240–1,000 | 2 | Districts, policies; fire, police; Unique L1; farming/forestry |
| 3 | Tiny Town | 360–1,600 | 2 | Industry areas, park areas, pedestrian areas; Unique L2; fishing |
| 4 | Boom Town | 650–2,800 | 3 | Airport areas; bus, ferry, tram, taxi, heating; Unique L3; ore |
| 5 | Busy Town | 1,200–5,500 | 3 | City-planning policies; trolleybus; Unique L4; oil; tourism spec. |
| 6 | Big Town | 1,800–8,000 | 4 | **Metro, high-density zones, office, tax policies**; Unique L5 |
| 7 | Small City | 2,600–11,000 | 4 | Train, monorail, cable car; Unique L6 |
| 8 | Big City | 4,000–18,000 | 5 | Liberal Arts College; Unique L7 |
| 9 | Grand City | 5,000–22,000 | 5 | 3rd loan |
| 10 | Capital City | 8,000–36,000 | 6 | Ship; University campus |
| 11 | Colossal City | 11,000–48,000 | 7 | Helicopter; nuclear plant |
| 12 | Metropolis | 16,000–70,000 | 8 | Plane; airport |
| 13 | Megalopolis | 20,000–90,000 | 9 | Monuments |

> All 13 base milestones have `m_autoAdjustTarget = 1` **[C]** — the population target
> **scales with the map's flatness and water**. Read the runtime target; do not hardcode
> the wiki numbers.

**Unique buildings:** one of each per city, unlocked L1 Worthy Village → L7 Big City.
Most require extra conditions to hold for a sustained period. Monuments each require a
specific unique building from each level.

---

## 9. Loans and bail-out **[C]**

**Loans** (max 3; unlocked at Little Hamlet / Worthy Village / Grand City):
```
periodsLeft = (amountLeft * length * 16 + amountTaken/2) / amountTaken
interestDue = amountTaken*rate/10000 - interestPaid
payment     = (amountLeft + interestDue + penalty) / periodsLeft
penalty     = max(0, (periodsLeft * (periodIndex+1) >> 4) - 1)
```

**Bail-out** — all must hold:
```
cash <= -10,000  AND  periodIncome < periodExpenses
AND  bailoutsAccepted < 1  AND  >= 4 weeks since last
```
Grants **₡20,000**, **once ever**, and **permanently disables achievements**.

---

## 10. Policies (selected, with numbers) **[C]/[W]**

| Policy | Effect |
|---|---|
| Small Business | low-density commercial income **×2**, cost 12/building |
| Big Business | high-density commercial income **×3**, cost 25 |
| Industrial Space Planning | **doubles** industrial goods, ₡6/wk/building |
| Industry 4.0 | **+50%** output, **−30%** workplaces |
| Boost Connections | outside-connection capacity **+20%**, ₡10,000/wk |
| Workers' Union | residential income **−2%** |
| Recreational Use | commercial income **+5%** |
| Fishing License | residential income **+5%**, +5% crime, −happiness |
| Recycling | garbage **−15%**, income **−5%** |
| For-Profit Education | education upkeep **−50%**, −happiness |
| Education Boost | education upkeep **+25%** |
| NIMBY / No Loud Noises | night leisure income **0**, utility use halved |
| Electric Cars | less noise, ₡2/vehicle/wk |
| Dolphin-Safe Fishing | +20% happiness, fish yield **−25%** |
| Free Transport | ridership up, **no ticket income** |
| High Ticket Prices | fares **+25%** |

Full lists live in `DistrictPolicies` (Services 32–60, Taxation 64–74,
CityPlanning 96–122, Park 192–199).

---

## 11. Top decision rules for the mayor

1. Demand moves **±2 per simulation step**. Judge outcomes a week apart, not a minute apart.
2. **R = C = I = 50** at map start — that's the seed value, not equilibrium.
3. **R ≥ 50** always while housing exists. It falls when unemployment rises or vacancies exceed unemployed.
4. **Target 1 commercial job per 8 citizens.** C demand = 0 below that ratio.
5. **I ≈ unemployment − unfilled vacancies.** I ≈ 0 with high unemployment ⇒ zone commercial/office, not industry.
6. **Keep tax at 9%** early. Each point above the ceiling = **−5 demand**. Below the ceiling = no bonus.
7. **101% service budget** = +1 vehicle per building, nearly free. Use it.
8. **Coverage follows roads.** A disconnected service covers nothing.
9. **Cargo transport (+1.0) > fire (+0.5) > police (+0.2)** for industrial level-ups. Fix cargo first.
10. **Residential level-up = land value + education.** Fix schools before services.
11. **Commercial level-up = land value + shopper wealth.** Upgrade residential first.
12. **Industrial/Office level-up = worker education + service coverage.** Never zone industry for uneducated workers.
13. **High-density residential ≈ 4× the households** of low density, but more traffic.
14. **Milestone population targets auto-adjust per map.** Read them at runtime.
15. **Metro arrives at Big Town**, high-density zones at Big Town, office at Big Town.
16. **A bus costs ₡12/week regardless of distance.** Distance drives fleet size.
17. **Highways block roadside zoning.** Pair with parallel streets.
18. **Bail-out at −₡10,000**: ₡20,000, once ever, **disables achievements permanently**. Never rely on it.
19. **Happiness = (health + wellbeing) / 2.** Education raises both happiness and employability.
20. **Unemployment drives crime**, and crime *and* I-demand move together.
21. **Zone only where roads exist.** Build the road, then zone beside it.
22. **A road crossing is not an intersection** unless a shared node exists. Reuse endpoints.
23. **Verify every action.** `dryRun=true` first; re-read the snapshot after.
24. **Save often**, and verify the `.crp` file was written.
25. **One decision per simulated week**, not per simulated hour.
