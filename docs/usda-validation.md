# USDA PLANTS traits validation

The first real USDA import ran on 2026-09-26 against the isolated PostgreSQL 17.9 validation database. That database already held the WFO 2026-09 backbone, its supplemental files, and the full Wikidata crosswalk and details, including 40,664 USDA PLANTS IDs (P1772). The source is the EOL "USDA PLANTS structured data DwCA", Zenodo record 18945513, version 8 (2026-03-10), extracted to `data/imports/usda/`. The `AddUsdaReference` migration applied in about a second.

## Import

`usda` completes in 23–29 s (import 10: 23.0 s). A plain rerun is skipped by the combined source hash. `--force` republishes with identical fact, distribution, link, and warning counts; it changes no link and keeps every `usda_taxon.Id`.

| File | SHA-256 | Rows |
| --- | --- | ---: |
| `taxon.tab` | `5d03b5a3…1e15970e` | 35,186 |
| `occurrence_specific.tab` | `71196612…5d250a` | 634,875 |
| `measurement_or_fact_specific.tab` | `84082cae…fba03f` | 580,161 |
| Combined source hash | `71f198fc…5a392808` | 1,250,222 |

`meta.xml` also declares `media_resource.tab`, `reference.tab`, and `agent.tab`. They are EOL template rows and were not in this extract; the importer logs them as ignored.

| Import | Count |
| --- | ---: |
| Rejected rows | 0 |
| Warnings | 8,055 (8,049 missing taxa, 5 unmapped codes, 1 canonical name) |
| Taxa (all `valid`): species / variety / genus / subspecies / form | 23,587 / 5,336 / 3,513 / 2,747 / 3 |
| Facts (`usda_fact`) | 251,235 |
| Distribution rows (`usda_distribution`), duplicates collapsed | 317,781, 0 |
| Facts skipped with their missing taxon | 11,145 |
| Distinct remarks stored | 84 |
| Unparseable numeric values; unknown measurement types | 0; 0 |
| Taxa with more than two heights | 0 |

251,235 facts + 317,781 distribution rows + 11,145 skipped = 580,161, every fact row read. The USDA tables and indexes use 170 MB after compaction.

**Taxa with characteristics** (a `shade_tolerance` or `min_temperature_f` fact): **2,011**. The spec expected about 2,044. The other 33 belong to symbols missing from `taxon.tab` (next section).

## Source quirks found in this release

- **Occurrences for symbols missing from `taxon.tab`.** 18,284 occurrences (11,145 facts, mostly growth habit and duration) point at 8,049 `taxonID`s that `taxon.tab` does not list. USDA's service shows what they are: ACGR is an old synonym symbol (now SEGR4, *Senegalia greggii*), and ABGU (*Abies guatemalensis*) is a current species EOL left out. A few are malformed (numeric IDs such as `24441`, and `http://eol.org/schema/terms/vine`). The spec treated these as rejections, which would block every import of this release. The importer instead skips them with one warning per symbol (occurrence and fact counts in the message) and reports counts and 20 examples. Facts pointing at unknown occurrences, duplicate symbols, and duplicate occurrence IDs still fail publication.
- **Georgia.** `Present` uses GeoNames 614540, the country of Georgia (6,046 rows), for the US state (4197000, never used). The fact's own remark says "Georgia", and the distribution row now keeps that remark. `place_label` labels 614540 as GeoNames does, so the catalog must translate it.
- **Alaska twice.** `Present` names Alaska as `https://www.wikidata.org/entity/Q797` (2,969 rows, the only `https` place URI); native and introduced ranges use GeoNames 5879092.
- **Distribution remarks name the place.** Examples: "Lower 48 United States of America" (Q578170), "Navassa Island (The sole Caribbean member of the United States Minor Outlying Islands)" (5854968), and "North America (only non-vascular plants and lichens have Native Status given at this level)" (6255149). `usda_distribution.RemarkId` keeps them. This column goes beyond the spec's schema.
- **Heights: the larger value is not always the mature height.** `measurementMethod` and `statisticalMethod` tell the two heights apart: SIO_001110 is "Expected height of plant at maturity" (2,023 facts), and SIO_001114 is "Maximum height … at a base age" (841 facts). In the 203-taxon verify sample, USDA's "Height, Mature (feet)" equals the SIO_001110 value for 201 of 201 taxa, but the maximum only for 190 of 201. Examples: VIOB 12 ft mature vs 30 ft at 20 years; CICH 10 vs 23. The catalog should select mature height by statistical method, not by maximum. `usda_fact.MethodRemarkId` keeps the method text; this goes beyond the spec's schema.
- **Nitrogen fixation "None" is not exported.** The archive has only 181 Low/Medium/High facts, so USDA's "None" appears as missing (185 of 203 sampled taxa).
- **`allelopathyUnknown` is USDA's "No".** USDA shows "Known Allelopath: No" for all 195 sampled facts with this code, so the seed labels it that way.
- **Old families** (e.g. ACSA3 is in `Aceraceae`) are stored as `FamilyUsda` only.
- **Positional IDs.** `measurementID` values (`M1`…) and the `O`-numbered occurrence IDs are positional. 362,045 occurrence IDs are hashes (`…_727`); neither kind is used as a durable key.
- **Unstored columns.** `bibliographicCitation` (218,116 rows) is the same PLANTS citation boilerplate; it is counted in the report but not stored. Every other unstored column is empty in this release.
- **Canonical names.** One name has no rank marker for its rank: ECROR, "Echinocereus ×roetteri (Englem.) Rumpler ×roetteri", a variety. It gets a null canonical name and a warning. Hybrid names follow WFO spelling ("Quercus × bebbiana"), a dotted epithet loses the dot ("Cyanea st-johnii"), and an infraspecific hybrid sign is dropped ("Senna artemisioides subsp. coriacea").

## Labels

The seed has 61 trait types, 147 code labels, and 75 place labels. After the verify runs below, 129 labels are `verified`, 15 `inferred`, and 3 `unresolved` (shade).

| Unmapped or unresolved | Facts |
| --- | ---: |
| `growth_habit_flopo`: five FLOPO habit codes, deliberately not mapped (redundant with `growth_habit`) | 38,754 |
| `shade_tolerance`: `PATO_0002394` / `PATO_0000461` / `PATO_0002393`, unresolved by design | 938 / 564 / 464 |

Every other trait value has a label.

## Distribution

| Kind | Rows |
| --- | ---: |
| Present | 260,987 |
| Native | 43,352 |
| Introduced | 13,442 |

Places are GeoNames IDs (283,031 rows), Wikidata QIDs (33,370: Q578170 and Q797), or the literal "Pacific Basin excluding Hawaii" (1,380). Every GeoNames and Wikidata place has a label.

- Taxa present in Ohio: **4,879**
- Present in Ohio **and** native to the contiguous US (Q578170): **3,653**

USDA gives state *presence* and native status for regions such as the Lower 48. The second number is therefore "present in Ohio and native somewhere in the Lower 48", not "native to Ohio".

## Links to WFO

`usda link` takes about 4 s on the full snapshot. A `wfo` run relinks after Wikidata re-resolution; with the backbone skipped, it changed no link.

| Status / method | Symbols |
| --- | ---: |
| Linked / wikidata | 32,541 |
| Linked / name | 1,189 |
| Conflict / wikidata | 26 |
| Ambiguous / name | 290 |
| NotFound / name | 1,140 |

- 32,618 current symbols appear as a P1772 value on a Wikidata item. 33,730 are linked; 32,983 of those have an accepted WFO taxon, and 7,496 link to a WFO synonym.
- In 32,059 of the 32,541 Wikidata links, the linked WFO name equals the USDA canonical name.
- **Conflicts:** Wikidata items that give a symbol more than one resolved WFO taxon, e.g. ABGR4 (*Abelia × grandiflora*: wfo-0000510752, wfo-0000510753). In 5 of the 26 conflicts, all candidates share one accepted taxon.
- **Ambiguous:** several current WFO names at the rank, e.g. ACPA14 *Acaena pallida*, which is `Accepted` (Kirk) Allan and `Unchecked` J.W.Dawson. In 89 of the 290, exactly one candidate is `Accepted`, and `DetailJson` records every candidate's authorship. Nothing is guessed; overrides belong in the catalog spec.
- **Not found:** mostly infraspecific autonyms WFO has no record for (517 varieties and 267 subspecies, e.g. *Lotus crassifolius* var. *crassifolius*), plus 323 species (e.g. USDA's *Brachychiton populneum*) and 32 genera.

## `usda verify`

Verify uses USDA's JSON backend at `plantsservices.sc.egov.usda.gov`, one request at a time, at least 500 ms apart. `PlantProfile?symbol=` returns the internal `Id` (0 for an unknown symbol); `PlantCharacteristics/{Id}` returns an array of `{PlantCharacteristicName, PlantCharacteristicValue, PlantCharacteristicCategory, CultivarName, SynonymName}`. Both ACSA3 responses are saved under `tests/…/Fixtures/usda/api/`.

The runs below compare characteristics, plus duration and growth habit from the profile. They cover 60 trait keys, counting the two height checks by statistical method, which were added after the first run.

| Run | Taxa | Comparisons agreeing | Label disagreements | Numeric disagreements | Result |
| --- | ---: | ---: | --- | ---: | --- |
| `--sample 20` (seed 42), initial seed | 24 | 1,609 | 20 × `allelopathyUnknown` (inferred) | 3 (mature height by maximum) | passed |
| `--sample 200 --seed 7`, initial seed | 203 | 13,816 | 195 × `allelopathyUnknown` (inferred) | 11 (mature height by maximum) | passed |
| `--sample 20` (seed 42), final seed | 24 | 1,658 | none | 3 (mature height by maximum) | passed |

- **No `verified` code disagreed** in any run.
- **PATO_0002393 = High (tolerance) is confirmed:** it agrees on 124 taxa across the non-shade tolerance traits (fire, drought, CaCO3, anaerobic, salinity, hedge), with no disagreements. The spec's bar is 3.
- **Promotion rule applied to the seed:** an `inferred` code became `verified` when it agreed on at least 3 sampled taxa with no disagreement, the same bar the spec set for PATO_0002393. That promoted 97 codes, with the counts cited in `evidence`. `allelopathyUnknown` was relabeled "Known Allelopath: No" (195 of 195).
- Six previously unmapped codes were each shown with a single USDA value. Five were added as `verified`: `C48658` Indeterminate (bloom period, 7 taxa), `PATO_0000631` Prostrate (9), `PATO_0002397` Rounded (7), `PATO_0002021` Conical (5), and `C63815` Irregular (3). `PATO_0001891` Oval (1 taxon) was added as `inferred`.
- Still `inferred` for lack of samples (fewer than 3 taxa, or none): `PATO_0000396` Severe, `PATO_0001891` Oval, `lateWinter`, `highNitrogenFixation`, `vaseShaped`, `columnar`, `lichenous`, `nonvascular`, `propagatedByBulbsYes`, `propagatedByCormsYes`, the two federal status codes, and the three Garden Persistent place literals (none of which USDA's characteristics show).
- USDA fields with no archive counterpart: the Suitability/Use products except Fuelwood, and Palatable Human.

## Shade tolerance (spec 2.2.1)

**Verify result.** Scored with the tolerance labels used by every other trait:

| Sample | Compared | Direct agrees | Inverted agrees |
| --- | ---: | ---: | ---: |
| 24 taxa (seed 42) | 24 | 6 | 24 |
| 203 taxa (seed 7) | 199 | 54 | 199 |

The direct agreements are all `PATO_0000461` (Medium), which reads the same under both hypotheses. Every Low/High value in USDA's live service is the opposite of the archive's code.

**plants.usda.gov in a browser (ACSA3):** *not checked in this session; no browser was available.* The page renders the same JSON service, which returns "Shade Tolerance: Low" for ACSA3. A human should confirm what the page displays.

**Independent spot check.** Missouri Botanical Garden Plant Finder, "Sun" field, retrieved 2026-09-26:

| Symbol | Species | Archive code (direct reading) | USDA service | Plant Finder |
| --- | --- | --- | --- | --- |
| ACSA3 | *Acer saccharum* | PATO_0002393 (High) | Low | Full sun to part shade |
| COFL2 | *Cornus florida* | PATO_0002393 (High) | Low | Full sun to part shade |
| TSCA | *Tsuga canadensis* | PATO_0002393 (High) | | Part shade to full shade |
| FAGR | *Fagus grandifolia* | PATO_0002393 (High) | | Full sun to part shade |
| CECA4 | *Cercis canadensis* | PATO_0002393 (High) | | Full sun to part shade |
| RUHI2 | *Rudbeckia hirta* | PATO_0002394 (Low) | High | Full sun |
| ECPU | *Echinacea purpurea* | PATO_0002394 (Low) | High | Full sun to part shade |
| ASTU | *Asclepias tuberosa* | PATO_0002394 (Low) | | Full sun |
| SCSC | *Schizachyrium scoparium* | PATO_0002394 (Low) | | Full sun |
| PIBA2 | *Pinus banksiana* | PATO_0002394 (Low) | | Full sun |

Every PATO_0002393 species tolerates shade; hemlock is the textbook example. Four of the five PATO_0002394 species are full sun only; purple coneflower also takes part shade. The canopy trees' "full sun to part shade" undersells how shade-tolerant sugar maple and beech are as seedlings, but no species contradicts the direct reading.

**Recommendation.** Adopt the **direct** hypothesis: shade codes mean what they mean in every other tolerance trait (PATO_0002394 Low, PATO_0000461 Medium, PATO_0002393 High). USDA's live service, and presumably plants.usda.gov, shows shade tolerance inverted. To adopt it, replace the three `unresolved` ShadeTolerance override rows in `data/reference/usda-code-labels.csv` with labeled rows that cite this section, and rerun `usda`. **No shade labels are seeded until a human decides,** and the catalog must not show sun or shade from USDA before then.

## ACSA3 end to end

`reference.usda_fact_labeled` for sugar maple, abbreviated:

| Trait | Value | Confidence |
| --- | --- | --- |
| fire_tolerance | Low (tolerance) | verified |
| drought_tolerance / caco3_tolerance | Medium (tolerance) | verified |
| shade_tolerance | PATO_0002393 (unresolved) | unresolved |
| min_temperature_f | -47 (°F, SIO_001113) | |
| height_ft | 100 (SIO_001110, mature) and 20 (SIO_001114, at 20 years) | |
| bloom_period | Mid Spring | verified |
| flower_color / foliage_color | Green | verified |
| growth_habit | Shrub, Tree | verified |
| leaf_retention | Deciduous (leaf retention: No) | verified |
| soil_ph | 3.7 (minimum), 7.9 (maximum) | |
| foliage_porosity_summer / winter | Dense / Porous | verified |
| known_allelopath | Known Allelopath: Yes | verified |

ACSA3 links through Wikidata to the accepted WFO *Acer saccharum*.

## Test runs

All 249 tests passed with both `ACBF_TEST_POSTGRES` and `ACBF_TEST_WFO_SNAPSHOT` set, including 67 new USDA tests and the full-snapshot USDA smoke test. `dotnet build` produced no warnings. No test contacts USDA.
