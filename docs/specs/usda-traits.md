# Spec: USDA PLANTS trait import (EOL archive)

## Goal

Stage the USDA PLANTS data from the EOL Darwin Core Archive into the `reference` schema, exactly as the source states it. That data is:
- about 35k taxa
- about 580k facts: growth habit, duration, characteristics, and native/introduced/present distribution

Then add two things around it:
- a **maintained, verified mapping** that turns the archive's ontology codes into readable values
- a **link from each USDA symbol to WFO taxa**

This is staging only. The curated catalog, zone calculation, and trait precedence rules are a later spec.

## Dependency

Build this **after** the Wikidata crosswalk spec. It reuses:
- `reference.source_import`, including its lifecycle and advisory locking
- the shared name-normalization helper
- `reference.wikidata_external_id` (P1772 = USDA PLANTS ID) for linking

## Source

- **Dataset:** "USDA PLANTS structured data DwCA", published by the Encyclopedia of Life, Zenodo record `18945513`, version 8 (2026-03-10). The file is `usda_plant_traits.tar.gz`. The underlying data is USDA NRCS PLANTS, a US government work.
- **Staging location:** extract under `data/imports/usda/`. That folder is Git-ignored except for `.gitkeep`, the same as WFO. The importer never modifies source files.
- **Files used:** `meta.xml`, `taxon.tab`, `occurrence_specific.tab`, `measurement_or_fact_specific.tab`.
- **Files ignored:** `agent.tab`, `reference.tab`, and `media_resource.tab`. These are leftover EOL template rows (a polar bear, a death cap mushroom), not USDA data. The importer logs that it ignored them.

### Observed facts about this release

These were confirmed by inspecting the files and should be encoded as tests or validation checks.

- `taxon.tab` has 35,186 rows, all with `taxonomicStatus = valid`. Ranks: species 23,587; variety 5,336; genus 3,513; subspecies 2,747; form 3.
- `taxonID` is the USDA symbol, e.g. `ACSA3`. `scientificName` includes the authorship, e.g. `Acer saccharum Marshall`. `family` uses **older classifications** (e.g. `Aceraceae`); never use it downstream.
- `occurrence_specific.tab` has 634,875 rows. `bodyPart` and `lifeStage` are mostly empty. The non-empty combinations are:
  - `UBERON_0000468` + `PATO_0001701` (whole organism, mature), on height facts
  - `PO_0025034` (leaf), on leaf retention
  - `PPO_0001007`, on seedling vigor
- `measurement_or_fact_specific.tab` has 580,161 rows. `measurementValue` is an ontology URI, a camelCase literal (e.g. `midSpring`, `fallConspicuousYes`), or a number. Units are URIs: inch, foot, per acre, days (`UO_0000033`), and °F (`UO_0000195`).
- `measurementRemarks` repeats long boilerplate text. It sometimes starts with `Source term: <USDA field>.` Remarks contain literal `\n` character sequences.
- **Height (`TO_0000207`)** appears once or twice per taxon with identical context. When there are two values, one is "height at 20 years" and one is "mature height." The mature height is the larger. Stage both as-is; the rule is applied in the catalog.
- **Multi-valued traits are normal.** Examples: `ACSA3` growth habit is `tree` and `shrub`; some taxa are both `annual` and `perennial`.
- **Distribution** comes from three `measurementType`s:
  - `Present`: about 261k rows. Values are GeoNames IDs, mostly US states.
  - `NativeRange` and `IntroducedRange`: values are GeoNames IDs, the Wikidata QID `Q578170` (contiguous US), or literals such as `Pacific Basin excluding Hawaii`.
- `measurementID` values (`M1`, `M2`, …) are positional and **not stable across releases**. Never use them as durable keys.

---

## 1. Schema (migration `AddUsdaReference`)

All tables are in the `reference` schema, with the same EF conventions as before.

### `usda_taxon`

| Column | Type | Notes |
|---|---|---|
| `id` | bigint identity | |
| `symbol` | text, unique | `taxonID` |
| `scientific_name` | text | Raw, including authorship |
| `canonical_name` | text null | Section 4.1 |
| `family_usda` | text null | Raw. Named so it's never mistaken for the real family. |
| `taxon_rank` | text null | |
| `taxonomic_status` | text null | |
| `source_url` | text null | |
| `is_current` | bool | |
| `import_id` | bigint FK → `source_import` | |

### `usda_fact`

All non-distribution facts go here, replaced wholesale on each successful import.

| Column | Type | Notes |
|---|---|---|
| `id` | bigint identity | |
| `usda_taxon_id` | bigint FK | |
| `symbol` | text | Denormalized for easy querying |
| `occurrence_id` | text | Raw; only unique within an import |
| `type_uri` | text | Raw `measurementType` |
| `value_raw` | text | Raw `measurementValue` |
| `value_numeric` | numeric null | Parsed with the invariant culture when the value is a number |
| `unit_uri` | text null | |
| `body_part_uri`, `life_stage_uri` | text null | From the occurrence |
| `statistical_method` | text null | |
| `source_term` | text null | Parsed from `Source term: X.` at the start of the remarks |
| `remark_id` | bigint FK → `usda_remark`, null | |
| `source_url` | text null | |
| `import_id` | bigint FK | |

Indexes: `(symbol)`, `(type_uri)`, `(type_uri, value_raw)`.

### `usda_remark`

Deduplicated boilerplate: `id`, `sha256` (unique), `text`.

### `usda_distribution`

For `Present`, `NativeRange`, and `IntroducedRange` facts, also replaced wholesale.

| Column | Type | Notes |
|---|---|---|
| `usda_taxon_id`, `symbol` | | |
| `kind` | text | `Present`, `Native`, or `Introduced` |
| `place_raw` | text | The raw value |
| `place_scheme` | text | `geonames`, `wikidata`, or `literal`. See the rule below. |
| `place_id` | text null | e.g. `5165418` or `Q578170` |
| `import_id` | | |

Unique on `(usda_taxon_id, kind, place_raw)`. Index on `(kind, place_id)`.

Classification rule: all digits → `geonames`; matches `^Q\d+$` → `wikidata`; a URI → take the last path segment and apply the same two rules; otherwise → `literal`.

### `usda_wfo_link`

| Column | Type | Notes |
|---|---|---|
| `usda_taxon_id`, `symbol` | | |
| `wfo_taxon_id` | bigint FK → `wfo_taxon`, null | Current taxon |
| `accepted_wfo_taxon_id` | bigint FK, null | Same rule as the Wikidata spec §3.3 |
| `method` | text | `wikidata` or `name` |
| `status` | text | `Linked`, `Ambiguous`, `NotFound`, or `Conflict` |
| `detail_json` | jsonb null | Candidate WFO IDs when `Ambiguous` or `Conflict` |

Unique on `(usda_taxon_id)`.

### Mapping tables (loaded from seed files in the repo on every import)

- **`usda_trait_type`**: `type_uri` (PK), `key` (stable snake_case, e.g. `shade_tolerance`), `label`, `value_kind` (`coded`, `numeric`, `literal`, or `multi`), `notes`.
- **`usda_code_label`**: `code` (PK, the value URI's last segment or the literal), `type_uri` (nullable; set only when a code means something different for a specific trait), `label`, `ordinal` (nullable, for Low/Medium/High ordering), `confidence` (`verified` or `inferred`), and `evidence` (e.g. `ACSA3,COFL2 via USDA API 2026-09-25`).
- **`place_label`**: `scheme`, `place_id`, `name`, `kind` (`state`, `province`, `country`, `region`), `country_code`, `admin_code` (e.g. `OH`).

The seed files are `data/reference/usda-trait-types.csv`, `data/reference/usda-code-labels.csv`, and `data/reference/place-labels.csv`. They are **committed** and human-reviewed. The importer upserts them each run, and changes show up in Git diffs.

- A **view** `reference.usda_fact_labeled` joins facts to type keys and labels, for inspection only.

---

## 2. Seed content

### 2.1 Trait types

Include every `measurementType` observed in this release (61 non-distribution types). The key ones:

| `type_uri` suffix | key | value_kind |
|---|---|---|
| `TO_0002725` | `duration` | multi |
| `PlantHabit` | `growth_habit` | multi |
| `FLOPO_0900032` | `growth_habit_flopo` | multi (redundant coded habit; keep, don't map yet) |
| `ShadeTolerance` | `shade_tolerance` | coded |
| `TO_0000276` | `drought_tolerance` | coded |
| `MoistureUse` | `moisture_use` | coded |
| `TemperatureTolerance` | `min_temperature_f` | numeric |
| `TO_0000207` | `height_ft` | numeric (1–2 values; mature = max) |
| `BloomPeriod` | `bloom_period` | coded |
| `TO_0000537` | `flower_color` | coded |
| `TO_0000326` | `foliage_color` | coded |
| `FruitSeedColor` | `fruit_seed_color` | coded |
| `PATO_0001729` | `leaf_retention` | coded |
| `BrowseAnimalPalatability` | `browse_palatability` | coded |
| `GrazeAnimalPalatability` | `graze_palatability` | coded |
| `HumanLivestockToxicity` | `toxicity` | coded |
| `260865002` | `growth_rate` | coded |
| `PATO_0000050` | `lifespan` | coded |
| `PATO_0000052` | `shape_orientation` | coded |
| `humanAgriculture.owl#Horticulture` | `horticulture_flags` | literal (e.g. `flowerConspicuousYes`) |
| `SoilPH` | `soil_ph` | numeric (min and max values) |
| `SoilRequirements` | `soil_texture_flags` | literal |
| `ThreatenedEndangeredStatus` | `federal_status` | literal |

Keep every other observed type with a sensible key.

### 2.2 Code labels

**Verified values.** These were checked on 2026-09-25 against USDA's labeled characteristics for ACSA3 (sugar maple), COFL2 (flowering dogwood), RUHI2 (black-eyed Susan), and ECPU (purple coneflower). Mark them `confidence = verified`.

| code | label | ordinal |
|---|---|---|
| `260413007` | None | 0 |
| `C54722` | Low | 1 |
| `C49507` | Medium | 2 |
| `C25227` | High | 3 |
| `PATO_0002394` | Low (tolerance) | 1 |
| `PATO_0000461` | Medium (tolerance) | 2 |
| `PATO_0000911` | Slow | |
| `PATO_0000912` | Rapid | |
| `PATO_0001603` | Long | |
| `PATO_0001604` | Short | |
| `PATO_0001731` | Deciduous (leaf retention: No) | |
| `PATO_0001733` | Evergreen (leaf retention: Yes) | |
| `PATO_0000700` | Coarse | |
| `PATO_0000701` | Fine | |
| `PATO_0000622` | Erect | |
| `PATO_0002260` | Semi-Erect | |
| `C94730` | Winter | |
| `C94731` | Spring | |
| `C94732` | Summer | |
| `C94733` | Fall | |
| `PATO_0000320` | Green | |
| `PATO_0000322` | Red | |
| `PATO_0000323` | White | |
| `PATO_0000317` | Black | |
| `PATO_0000951` | Purple | |
| `PATO_0000952` | Brown | |
| `PATO_0000953` | Orange | |

The two tolerance codes above were verified only through non-shade fields: `PATO_0002394` = Low via maple's Fire Tolerance, and `PATO_0000461` = Medium via maple's Drought and CaCO₃ Tolerance. They apply to every tolerance trait **except** `shade_tolerance`; see §2.2.1.

**Inferred values** (`confidence = inferred`, until the verify command confirms them):
- `PATO_0002393` High (tolerance), ordinal 3. Inferred by elimination as the third member of the tolerance set; never directly matched to a USDA label. Promote it once `usda verify` confirms it on a non-shade tolerance field (fire, drought, CaCO₃, anaerobic, salinity, or hedge) for at least 3 taxa.
- `PATO_0000324` Yellow
- `PATO_0000318` Blue
- `PATO_0000394` Slight, `PATO_0000395` Moderate, `PATO_0000396` Severe, each with `type_uri` = `HumanLivestockToxicity`
- `PATO_0000984` Porous

**Literals:** camelCase values get labels by splitting the camelCase, e.g. `lateSpring` → "Late Spring". Store them explicitly in the seed anyway so labels are reviewable.

**Unknown codes** (`PATO_0002021`, `PATO_0001891`, `PATO_0000631`, `PATO_0002397`, `C48658`, `C63815`, and any others): leave them unmapped until verified. The import reports them.

#### 2.2.1 Shade tolerance is unresolved

**Evidence gathered 2026-09-26.** The archive and USDA's live service disagree, consistently and in opposite directions:

| Taxon | Archive code | USDA service label (raw JSON) | Known ecology |
|---|---|---|---|
| ACSA3 sugar maple | `PATO_0002393` | Low | Very shade tolerant |
| COFL2 flowering dogwood | `PATO_0002393` | Low | Shade tolerant (understory tree) |
| RUHI2 black-eyed Susan | `PATO_0002394` | High | Full sun |
| ECPU purple coneflower | `PATO_0002394` | High | Full sun |

- Read with the tolerance labels used everywhere else, the archive matches ecology and the live service is inverted.
- It is **not known** which source changed. EOL may have flipped the field on export, or USDA's current site may have. Don't assume either.

**Rules:**
- The seed maps **no** labels for `shade_tolerance`. Add a `usda_code_label` override mechanism: rows with `type_uri` = ShadeTolerance take precedence over generic rows for that trait. Seed those override rows with `label = NULL` and `confidence = unresolved`, so the generic tolerance labels never apply to shade by accident.
- `usda_fact_labeled` shows shade values as the raw code plus `unresolved`.
- `usda verify` reports `shade_tolerance` in its own section: agreement counts for both hypotheses (**direct**: the code means the same as in other tolerance fields; **inverted**) across the whole sample. It never fails the run over shade.
- The validation doc records:
  - the verify result
  - what plants.usda.gov shows in a browser for ACSA3
  - a spot check of at least 5 well-known species against an independent source (e.g. Missouri Botanical Garden Plant Finder or a state extension fact sheet), recording sun/shade by hand
- Seed shade labels only after a human decides which hypothesis holds, citing that evidence in the `evidence` column. Until then, the catalog must not show sun/shade from USDA.

### 2.3 Place labels

Generate the seed once from GeoNames' `admin1CodesASCII.txt` and `countryInfo.txt` (CC BY 4.0). Include:
- the US states and DC
- Canadian provinces and territories
- the US territories present in the data
- countries appearing in the data

Add `Q578170` → "Contiguous United States" (scheme `wikidata`). Commit the generator script under `tools/` alongside the CSV.

**Test:** Ohio's GeoNames ID is present, with `admin_code = OH`. Also test that every GeoNames ID in `usda_distribution` has a label, or is reported.

---

## 3. Import command

```
dotnet run --project src/AnythingCanBeFarming.DataImport -- usda [--directory data/imports/usda/usda_plant_traits] [--force] [--version 8] [--zenodo-record 18945513]
```

1. **Discover and validate** `meta.xml`. Confirm the core/extension row types and column orders match the expected headers. Fail clearly on any difference.
2. **Hash** the three data files. The combined SHA-256 over the file hashes in a fixed order is the import's source hash. An identical successful hash is skipped unless `--force` is passed.
3. **Load the seed CSVs**, upserting the mapping tables. This happens even when the data import is skipped.
4. **Stream-parse** with CsvHelper in TSV mode, streaming through binary COPY into temp staging tables, as the WFO importer does.
   - **Required fields:** `taxonID` and `scientificName` for taxa; `occurrenceID` and `taxonID` for occurrences; `occurrenceID` and `measurementType` for facts.
   - **Rejections that fail publication:** missing required fields, duplicate symbols, occurrences pointing at unknown taxa, facts pointing at unknown occurrences.
   - **Warnings:** unparseable numerics where `value_kind = numeric`, unknown type URIs, unmapped coded values.
5. **Publish atomically** in one transaction:
   - upsert `usda_taxon` by symbol, retiring absent symbols (`is_current = false`)
   - replace `usda_fact` and `usda_distribution` entirely
   - dedupe remarks
   - rebuild `usda_wfo_link` (section 4)
   - write the `source_import` row (source `USDA`, kind `EolTraits`) with parameters including the Zenodo record and version
6. **Diagnostics:** row-level problems stream to an adjacent `usda-import-*.jsonl` file, the same as WFO.

`wfo all` gains a final step that re-runs `usda link` (section 4) after backbone changes. `usda link` also exists as a standalone command.

---

## 4. Linking USDA symbols to WFO

### 4.1 Canonical name

Derive `canonical_name` from `scientific_name` and `taxon_rank`, without the authorship:

- **genus:** the first token
- **species:** genus + epithet, the first two tokens. A hybrid marker `×` or `x` between them is kept as `×`.
- **variety / subspecies / form:** genus + epithet + the marker (`var.`, `ssp.` → `subsp.`, `f.`) + the infraspecific epithet, taken from the token after the marker. Ignore the authorship between the epithet and the marker.

Unparseable names get null and a warning. Add unit tests for:
- `Acer saccharum Marshall`
- `Abies grandis (Douglas ex D. Don) Lindl.`
- `Acer saccharum Marshall var. nigrum (F. Michx.) Britton`
- `Quercus ×bebbiana C.K. Schneid.`
- a genus
- a `ssp.` name

### 4.2 Link precedence

1. **Wikidata:** find current `wikidata_external_id` rows with `property = 'P1772'` and `value = symbol`. The item's current WFO link gives the resolved taxon.
   - If exactly one distinct resolved current WFO taxon → `Linked` with method `wikidata`.
   - If several → `Conflict`, with the candidates recorded.
2. **Name fallback** (only when step 1 found nothing): match `canonical_name` against the current `wfo_taxon.scientific_name` with the same rank. Compare case-sensitively after the shared normalization helper.
   - Exactly one match → `Linked` with method `name`.
   - More than one → `Ambiguous`.
   - None → `NotFound`.
3. **Accepted taxon:** set `accepted_wfo_taxon_id` using the Wikidata spec's §3.3 rule. A USDA name that is a WFO synonym links to its accepted taxon this way.

Never guess among ambiguous candidates. Manual overrides come in the catalog spec.

---

## 5. Verification against USDA (`usda verify`)

```
dotnet run --project src/AnythingCanBeFarming.DataImport -- usda verify [--sample 20] [--symbols ACSA3,COFL2,...] [--seed 42]
```

- **This uses USDA's unofficial JSON backend.** Treat it as a verification aid only; the import never depends on it.
  - `GET https://plantsservices.sc.egov.usda.gov/api/PlantProfile?symbol={symbol}` returns the internal `Id` and `HasCharacteristics`.
  - `GET https://plantsservices.sc.egov.usda.gov/api/PlantCharacteristics/{Id}` returns labeled characteristics.
- **Before implementing, inspect one real response** and write the parser against its actual JSON shape. Save one response as a test fixture.
- **Sampling:** the default is a deterministic random sample, seeded, of taxa with characteristics. **Always include** ACSA3, COFL2, RUHI2, and ECPU.
- **Comparison:** for each sampled taxon and each mapped coded trait (shade is handled separately per §2.2.1), compare our label (from `usda_fact_labeled`) with USDA's label. Use a field-name map from USDA's names to our keys, e.g. "Shade Tolerance" → `shade_tolerance`, "Flower Color" → `flower_color`, "Leaf Retention" → `leaf_retention` (Yes/No against evergreen/deciduous), "Toxicity" → `toxicity`. Compare case-insensitively.
  - **Numerics:** compare "Height, Mature (feet)" to the max of `height_ft`, and "Temperature, Minimum (°F)" to `min_temperature_f`.
- **Output:** a table of agreements and disagreements per trait key. Exit nonzero on any disagreement for a `verified` code. Report `inferred` codes that were confirmed, so the seed can be promoted.
- **Politeness:** one request at a time, at least 500 ms apart, with the same User-Agent convention as Wikidata.

---

## 6. Validation report

This is `validation_json` plus console output.

- File hashes; rows read per file; rejected rows; warnings by kind
- Taxa by rank; current and retired counts
- Facts by trait key; **taxa with characteristics**, i.e. taxa having `shade_tolerance` or `min_temperature_f` (expected about 2,044)
- Unmapped coded values by trait key, with counts; unknown type URIs
- Distribution rows by kind; taxa **present in Ohio**; taxa present in Ohio **and** native to the contiguous US
- Link status and method distribution; conflicts and ambiguities (count plus 20 examples)
- Taxa whose `height_ft` has more than 2 values (expected 0)

After the first real run, write `docs/usda-validation.md` containing the counts, the `usda verify` results (including the shade-tolerance conclusion), and the known quirks listed above.

---

## 7. Tests

- **Parsing:** `meta.xml` validation (good, reordered columns, missing file); TSV rows with literal `\n` in remarks; numeric parsing; `Source term` extraction; place classification.
- **Seeds:** seed CSVs load; unknown and duplicate codes are handled; every `verified` code in the seed is unique per `(code, type_uri)`.
- **Names:** the canonical-name cases in §4.1.
- **PostgreSQL:**
  - import, then a repeat skip, then a forced re-import
  - an absent symbol is retired
  - facts and distribution are replaced wholesale
  - a failed publish rolls back
  - link precedence: Wikidata over name; conflict; ambiguous; synonym to accepted
  - the relink after a WFO refresh
- **Verify:** stubbed HTTP using the saved fixture. Agreement and disagreement paths; the nonzero exit; the numeric comparisons.
- **Fixture archive:** a tiny hand-made one under `tests/.../Fixtures/usda/` containing ACSA3, COFL2, RUHI2, ECPU, plus a genus, a variety, and a multi-valued habit.

## 8. README

Add a "USDA PLANTS traits" section covering:
- the source and its citation (USDA NRCS PLANTS via EOL, Zenodo DOI)
- where to extract the archive
- the commands
- the seed files and how to update a code label
- `usda verify`
- the known quirks: old families, the height rule, the unresolved shade tolerance direction (§2.2.1), and the Ohio-native caveat (USDA gives state **presence** plus native status for the contiguous US, not per-state native status)

## 9. Acceptance

- [ ] A full import of the real archive succeeds. A rerun is skipped, and `--force` reproduces identical counts.
- [ ] Taxa with characteristics come to about 2,044.
- [ ] `usda verify --sample 20` passes for all `verified` codes, reports whether `PATO_0002393` = High was confirmed, and reports shade agreement under both hypotheses.
- [ ] `usda_fact_labeled` for ACSA3 reads sensibly end to end, e.g. fire tolerance Low, drought tolerance Medium, shade tolerance `PATO_0002393` (unresolved), min temp −47 °F, height 20 and 100, mid-spring bloom, green flowers.
- [ ] The validation doc contains the shade evidence (browser check plus independent spot checks) and a recommendation, but no shade labels are seeded without a human decision.
- [ ] The validation doc is written, the README is updated, `dotnet build` produces no warnings, and `dotnet test` passes.

## 10. Suggested order

1. Migration, entities, and seed file formats, plus the seed loader
2. Parsers (`meta.xml`, TSV, canonical names, place classification) with tests
3. Import publish, with PostgreSQL tests using the fixture archive
4. Linking, plus the `wfo all` hook
5. `usda verify`: inspect a real response first, then build it
6. Place-label generator and seed
7. Real import, verify run, validation doc, README
