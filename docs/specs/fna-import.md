# Spec: Flora of North America (FNA) import

## Goal

Stage the Flora of North America treatments from AAFC's two curation repositories into the `reference` schema, then link them to WFO.

Staged content, per treatment:
- names, synonyms, and status flags
- the botanical description
- phenology, habitat, and elevation
- distribution, per state or province, with native or introduced status
- the parsed characters from the fine-grained repository

FNA becomes the preferred source for four things:
- botanical descriptions
- bloom and fruiting months
- native vs. introduced status by state (the "native to Ohio" signal)
- introduced-and-weedy signals

Staging only. Display choices belong in the catalog spec.

## Sources

Both repositories are cloned locally. The importer never modifies them.

| Repo | Default path | Used content |
|---|---|---|
| `bitbucket.org/aafc-mbb/fna-data-curation` ("coarse") | `C:\data\fna-data-curation` | `coarse_grained_fna_xml/V*/*.xml` (27,052 files) |
| `bitbucket.org/aafc-mbb/fna-fine-grained-xml` ("fine") | `C:\data\fna-fine-grained-xml` | `V*/*.xml` (27,050 files; `V25_1674` and `V9_1140` are absent) |

- **Configuration:** paths come from `Fna:CoarseRepoPath` and `Fna:FineRepoPath`, overridable with `--coarse` and `--fine`.
- **Pinning:** record each repo's `HEAD` commit hash with `git rev-parse HEAD`. If git isn't available, read `.git/HEAD` and the ref file directly. The pair of hashes is the import's source identity. If both hashes match a previous successful import, skip the run unless `--force` is passed. Also record whether either working tree is dirty; if it is, that's a warning, not a failure.
- **Observed release:** both repos' last commits are dated 2026-04-24.
- **Ignored content:** everything else in both repos, including `supplemental_data/exiled_coarse_grained_fna_xml` (withdrawn treatments), `source_fna/`, and `documents/`. Ignore any folder named `_claude_tmp`.

### Licensing

From each repo's README:
- Taxonomy, morphological description, and distribution are **CC BY 4.0**, © Flora of North America Association. V24 and V25 are © Utah State University.
- Discussion is **reuse by request** (copyright@floranorthamerica.org).
- Commercial reuse requests go to the same address.

The README doesn't classify phenology, habitat, elevation, conservation notes, or keys. The owner is emailing FNA to confirm.

Store a license value on every text field (see the §1 enum). The catalog must not display anything whose license isn't CC BY until it's confirmed.

---

## 1. Schema (migration `AddFnaReference`)

All tables are in the `reference` schema.

### `fna_treatment`

One row per coarse XML file.

| Column | Type | Notes |
|---|---|---|
| `id` | bigint identity | |
| `file_id` | text, unique | e.g. `V13_209`, from the file name without its extension |
| `volume_folder` | text | e.g. `V13`, `V19-20-21` |
| `volume` | text | From `other_info_on_meta[@type='volume']`, e.g. `13` |
| `rank` | text | Rank of the last `taxon_name` in the accepted identification |
| `scientific_name` | text | The canonical name, built per §2.1 |
| `authority` | text null | The accepted name's last `taxon_name/@authority`. `unknown` is stored as null. |
| `family`, `genus` | text null | Title case, from the accepted hierarchy |
| `parent_file_id` | text null | See §2.3 |
| `treatment_number` | text null | `<number>` |
| `publication_title`, `publication_place` | text null | |
| `special_status` | text[] | Raw codes in source order, e.g. `{F,E}` |
| `is_introduced` | bool | Per §2.4 |
| `morphology` | text null | Concatenated `description[@type='morphology']` text, whitespace-trimmed but otherwise raw |
| `phenology`, `habitat`, `elevation`, `distribution_text`, `conservation` | text null | Raw, from the matching `description` types |
| `discussion` | text[] | Each `<discussion>` paragraph, raw |
| `key_xml` | text null | The raw `<key>` elements, serialized. Kept for a future identifier feature. |
| `mention_pages`, `treatment_page`, `illustrator`, `illustration_page` | text null | From `other_info_on_meta` |
| `fna_url` | text | `https://floranorthamerica.org/{scientific_name with spaces → _}` |
| `license_core` | text | `CC-BY-4.0`. Covers names, morphology, and distribution. |
| `license_phenology_habitat` | text | `Unconfirmed` until the owner updates the seed setting (§6) |
| `license_discussion` | text | `ReuseByRequest` |
| `license_key` | text | `Unconfirmed` |
| `copyright_holder` | text | `Utah State University` for V24/V25, otherwise `Flora of North America Association` |
| `is_current` | bool | False when the file disappears in a later import |
| `import_id` | bigint FK → `source_import` | |

Indexes: `scientific_name`, `(genus, rank)`, `parent_file_id`.

### `fna_name`

Every `taxon_identification`: accepted names, synonyms, and basionyms.

| Column | Type | Notes |
|---|---|---|
| `treatment_id` | bigint FK | |
| `status` | text | `ACCEPTED`, `SYNONYM`, or `BASIONYM` |
| `ordinal` | int | Position in the file |
| `name_raw` | text | The `taxon_hierarchy` text, raw |
| `canonical_name` | text null | Per §2.1, with abbreviated genera expanded per §2.2 |
| `rank` | text | |
| `authority` | text null | |

Index on `canonical_name`.

### `fna_common_name`

Columns: `treatment_id`, `kind` (`common_name`, `past_name`, `invalid_name`, or null), `name` (raw), and `normalized_name` (using the shared normalization helper).

### `fna_place`

Distribution, one row per place per treatment, taken from the **fine** file's parsed distribution characters.

| Column | Notes |
|---|---|
| `treatment_id` | |
| `place_raw` | e.g. `Ohio`, `Nfld. and Labr. (Nfld.)`, `Eurasia` |
| `constraint` | `United States`, `Canada`, or null (elsewhere) |
| `establishment_means` | `native` or `introduced`, raw |
| `admin_code` | Mapped per §2.5, e.g. `US-OH`; null outside the US and Canada |
| `source` | `fine`, or `coarse-parse` when the fine file is missing (§2.5) |

Unique on `(treatment_id, place_raw, establishment_means)`. Index on `(admin_code, establishment_means)`.

### `fna_character`

A raw copy of every parsed `<character>` in the fine files, about 1.67M rows.

| Column | Notes |
|---|---|
| `treatment_id` | |
| `description_type` | e.g. `morphology`, `phenology` |
| `statement_id` | |
| `statement_text` | |
| `entity_name` | `biological_entity/@name` |
| `entity_type` | |
| `entity_id` | |
| `name` | The character name |
| `value`, `from_value`, `to_value` | |
| `from_unit`, `to_unit`, `unit` | |
| `char_type`, `modifier`, `constraint` | |
| `is_modifier` | |
| `attributes_json` | Every other attribute, as jsonb |

Indexes: `(treatment_id)` and `(entity_name, name)`.

- **Relations:** stage `<relation>` elements the same way in `fna_relation`, about 82k rows. They aren't used yet.
- **Loading:** stream with a forward-only XML reader and binary COPY, as the other importers do. Don't build DOMs for 500 MB of files.

### `fna_phenology`

Derived per §2.6: `treatment_id`, `event` (`flowering`, `fruiting`, or `other`), `from_month` (1–12 or null), `to_month`, `season_text` (the original words), `year_round` (bool), `parse_status` (`Parsed`, `Partial`, or `Unparsed`), and `source_text`.

### `fna_wfo_link`

Same shape and statuses as `usda_wfo_link`, keyed by `treatment_id`. `method` is `name`, `name_authority`, or `synonym`.

### Seed tables (committed CSVs, upserted every run)

**`data/reference/fna-special-status.csv`** (`code`, `label`, `confidence`, `evidence`):

| code | label | confidence |
|---|---|---|
| E | Endemic (to North America north of Mexico) | verified: wiki label "Endemic" on Acer saccharum |
| F | Illustrated | verified: wiki label "Illustrated" on Acer saccharum |
| I | Introduced | inferred |
| C | Of conservation concern | inferred |
| W | Weedy | inferred |
| W1, W2, Y | *(null)* | unresolved. Found only in V3, V23, and V26 (W1/W2) and V14 (Y). W1 appears on *Quercus alba*, so it cannot mean "noxious weed". Resolve from the wiki's labels. |

**`data/reference/fna-place-abbreviations.csv`** (`abbreviation`, `admin_code`, `name`): every US state, DC, Canadian province and territory, and St. Pierre and Miquelon, in the forms FNA uses (e.g. `Ohio`, `N.Y.`, `Nfld. and Labr. (Nfld.)`, `Nfld. and Labr. (Labr.)`, `Yukon`). Build it from the distinct `place_raw` values where `constraint` is not null. The import reports any unmapped value.

---

## 2. Rules

### 2.1 Canonical names

Build names from the `taxon_name` elements, in document order.

- **Genus:** title case (`ACER` → `Acer`).
- **Species epithet:** lowercase, as given.
- **Infraspecific ranks:** marker plus epithet. The markers are `variety` → `var.`, `subspecies` → `subsp.`, `form` → `f.`, and `nothovariety` → `nothovar.`.
- **Ranks above genus** (family, subfamily, tribe, subtribe) and **within genus** (subgenus, section, subsection, series, unranked): `scientific_name` is the title-cased name of that rank alone, and `genus` records the parent genus where present.
- **Hybrid signs:** keep them as `×`, following the WFO spelling rules the USDA importer already uses.

### 2.2 Abbreviated synonym genera

A synonym's genus like `A.` expands to the treatment's accepted genus when the first letter matches (`A. saccharum var. glaucum` → `Acer saccharum var. glaucum`). Otherwise leave `canonical_name` null and record a warning.

### 2.3 Parents

For infraspecific treatments, `parent_file_id` is the treatment in the same import whose `scientific_name` equals the species part of the name.

For species, it's the genus treatment. If there's no match, leave it null and report it.

**Use:** 789 species state their distribution only on their infraspecific children. Cercis canadensis says "North America, n Mexico", and Ohio appears only under subsp. *canadensis*.

Provide a view `fna_place_rollup`: each species' places are the union of its own `fna_place` rows and its children's, with a `from_child` flag. A place counts as native if any contributing row says native.

### 2.4 Introduced flag

`is_introduced` is true when either condition holds:
- `special_status` contains `I`
- `distribution_text`, trimmed, starts with `Introduced`, case-insensitive

Infraspecific taxa often carry only the text form. About 1,100 treatments mention "introduced" without the `I` flag; many of those are infraspecific, and some are native taxa introduced *outside* North America. Only the prefix form counts.

### 2.5 Places

- **Fine files are the source.** Their distribution characters are already split per place and carry `establishment_means`.
- **Missing fine file** (2 files): parse the coarse `distribution_text` instead. Split segments on `;`. The first segments are Canadian places, then US places, then everything else; identify Canadian and US segments by matching every comma-separated token against the abbreviation seed. The establishment is `introduced` when `is_introduced`, otherwise `native`. Set `source = coarse-parse`.
- **Validation:** for every treatment that has both files, the set of US and Canadian places parsed from the coarse text must equal the fine set. Report differences, with up to 20 examples; they're warnings, not failures.

### 2.6 Phenology parsing

Parse the coarse `phenology` text deterministically. Don't use CharaParser's phenology characters: it misread *Pyrus calleryana* ("Flowering late Feb–early May (sometimes partial second flowering Sep–Oct)") as Sep–Oct.

- **Structure:** split on `;` into clauses. The event is the first word: Flowering, Fruiting, or other (Sporulating, Coning, Capsules, …).
- **Months:** `Jan`…`Dec`, and full month names, map to 1–12. `early`, `mid`, and `late` are allowed as prefixes; keep the words in `season_text`, but they don't change the month.
- **Ranges:** `X–Y` or `X-Y`. A lone month gives from = to.
- **Seasons:** spring = 3–5, summer = 6–8, fall/autumn = 9–11, winter = 12–2. Set `parse_status = Partial` because seasons are approximate, and keep the words.
  - A combined form like `mid-late summer` becomes summer, `Partial`.
- **Year-round:** `year-round`, `all year`, or `throughout the year` sets `year_round = true`.
- **Parentheticals** are secondary events. Ignore them for months but keep them in `source_text`.
- **Anything else** is `Unparsed`, with the text kept.

Tests must cover the examples above, plus `Flowering Apr–Jun; fruiting Jun–Sep.` and `Flowering late summer–early autumn.`

Report the parse rate. The expected share of simple matches among "Flowering" clauses is about 90%.

### 2.7 Linking to WFO

For each current treatment at genus rank or below:

1. **Exact name:** the accepted `scientific_name` equals a current `wfo_taxon.scientific_name` at the same rank.
   - One match → `Linked`, method `name`.
   - Several matches → compare `authority` after normalizing both authorships (collapse whitespace, remove spaces after periods, and compare case-insensitively). Exactly one match → `Linked`, method `name_authority`. Otherwise `Ambiguous`, recording the candidates.
2. **Synonyms:** if there's no exact match, try each FNA synonym and basionym `canonical_name` against current WFO names. Collect each match's accepted taxon (per Wikidata spec §3.3). Exactly one distinct accepted taxon → `Linked`, method `synonym`. Several → `Conflict`.
3. Otherwise → `NotFound`.
4. Set `accepted_wfo_taxon_id` as in the other link tables.

Never guess, and never link ranks above genus.

`wfo all` re-runs `fna link` after a backbone change, as it does for USDA.

---

## 3. Command

```
dotnet run --project src/AnythingCanBeFarming.DataImport -- fna [--coarse <path>] [--fine <path>] [--force]
dotnet run --project src/AnythingCanBeFarming.DataImport -- fna link
```

- **Publication:** one transaction replaces all `fna_*` data rows. Treatments keep their `id` by `file_id` across imports, and absent files are retired, not deleted. Then linking runs.
- **Rejections that fail publication:**
  - an unparseable XML file
  - a file with no ACCEPTED identification, or more than one
  - duplicate `file_id`s
- **Warnings:**
  - unmapped places or status codes
  - canonical-name failures
  - coarse/fine mismatches
  - a coarse file without a fine counterpart, or the reverse
  - unparsed phenology
- **Diagnostics:** stream to `fna-import-*.jsonl`, as the other importers do.

---

## 4. Validation report and doc

**Counts:**
- files per volume, for coarse and fine
- ranks
- statuses
- special-status codes by volume
- field coverage: morphology, phenology, habitat, elevation, distribution, discussion, keys, common names
- characters and relations staged

**Distribution:**
- the coarse/fine place-set agreement rate
- treatments whose US states mix native and introduced (expected: 1, `V22_74`)
- **Ohio**: treatments with Ohio native or introduced; species with Ohio native after roll-up (expected about 2,100 not introduced); and the comparison to USDA's "present in Ohio" (4,879) and "present in Ohio and native in the Lower 48" (3,653)

**Phenology:** parse status counts; the share of species with a flowering month range (own or rolled up from children).

**Links:** status and method counts, 20 examples each of Ambiguous, Conflict, and NotFound, and overlap with USDA links, meaning taxa linked by both.

**Garden check.** Print name, link, `is_introduced`, Ohio status after roll-up, flowering months, and the first 120 characters of morphology for:

*Acer saccharum*, *Acer palmatum*, *Rudbeckia hirta*, *Echinacea purpurea*, *Hosta*, *Hosta ventricosa*, *Cercis canadensis*, *Tsuga canadensis*, *Asclepias tuberosa*, *Alliaria petiolata*, *Hemerocallis fulva*, *Pyrus calleryana*, *Quercus alba*.

*Pyrus calleryana* must show flowering Feb–May.

**Known gaps to document:** FNA has no treatments for the Lamiaceae, Oleaceae, Aquifoliaceae, Caprifoliaceae, or Polemoniaceae (their volumes aren't in the repos), and it only covers plants that grow wild or naturalized in North America.

Write `docs/fna-validation.md` after the first real run.

## 5. Tests

**Fixtures:** copy real files for `V13_209` (*Acer saccharum*), *Cercis canadensis* and its three subspecies, *Pyrus calleryana*, a V24 file, `V22_74`, a genus, and a family, from both repos, into `tests/.../Fixtures/fna/`. Also add a hand-made file that has no fine counterpart.

**Unit tests:**
- canonical names, including hybrids and infraspecific names
- abbreviated-genus expansion
- the introduced rules
- the coarse distribution parse
- the phenology cases
- authorship normalization

**PostgreSQL tests:**
- import, then a repeat skip keyed on the commit hashes, then a forced reimport with stable IDs
- a retired file
- a failed publish rolls back
- the place roll-up
- link precedence: exact, then authority, then synonym; Ambiguous; Conflict
- relink after a WFO refresh

No test reads the real repos. An optional smoke test runs when `ACBF_TEST_FNA_REPOS` points at both clones.

## 6. Licensing configuration

`data/reference/fna-licenses.csv` sets the license values for `license_phenology_habitat` and `license_key`. They start as `Unconfirmed`. When FNA replies, the owner edits the CSV and re-imports.

The README must state the attribution the app will display:

"Flora of North America Editorial Committee, eds. Flora of North America North of Mexico, vol. {volume}. © {copyright_holder}. CC BY 4.0." followed by a link to `fna_url`.

## 7. Acceptance

- [ ] A full import of both clones succeeds. A rerun is skipped, and `--force` reproduces identical counts and IDs.
- [ ] The garden check reads correctly, including Callery pear flowering Feb–May and redbud native in Ohio through roll-up.
- [ ] The coarse/fine place agreement is at least 99%, with the differences listed.
- [ ] The validation doc is written, the README is updated, `dotnet build` produces no warnings, and `dotnet test` passes.

## 8. Order

1. Migration, entities, and seeds
2. Streaming readers for coarse and fine files; canonical names; fixtures and unit tests
3. Places (fine plus coarse fallback), roll-up view, and the introduced rules
4. Phenology parser
5. Publishing, with PostgreSQL tests
6. Linking, plus the `wfo all` hook
7. Real run, validation doc, and README
