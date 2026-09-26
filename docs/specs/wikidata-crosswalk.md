# Spec: Wikidata crosswalk and common names

## Goal

1. Link every WFO taxon that Wikidata knows about to its Wikidata item.
2. Pull English (and other-language) common names, a Wikipedia article link, an image reference, and the IDs other sources use: GBIF, IPNI, USDA PLANTS, and POWO.
3. Make reference search find plants by common name. "Japanese maple" must find *Acer palmatum*.

This is the first **enrichment source**. Its structure is reused by later importers (USDA PLANTS, WCVP, FNA, invasive lists), so the generic pieces below — `reference.source_import` and the resolution rules — are deliberately source-neutral.

## Out of scope

- Wikipedia text, descriptions, and garden traits. These are later specs; this one only stores the article title.
- The curated `catalog` schema that merges sources. That spec comes once at least two enrichment sources exist.
- Downloading or hosting images. Store the Commons file name only.
- Any UI beyond the existing search endpoint's response.

## Principles (same as the WFO importers)

- **Repeatable.** Running it twice with no upstream change adds no rows and changes no data. Re-running after upstream edits updates only what changed.
- **Atomic publication per phase.** A failed or cancelled run leaves previously published data intact and records failure metadata.
- **Stored as stated.** Raw Wikidata values are kept exactly. Normalized columns sit alongside them and never replace them.
- **Untrusted text.** All Wikidata strings are untrusted source text. Never render them as HTML.
- **Good citizen.** Every request sends a descriptive `User-Agent`, and requests are throttled. Details are below.

Wikidata data is CC0, so no attribution is legally required. Still record the source on every row; the app will credit sources anyway.

---

## 1. Schema (new migration `AddWikidataReference`)

All new tables live in the `reference` schema. Use the same EF conventions as the WFO entities: identity bigint keys, restrict deletes, and explicit indexes.

### `reference.source_import`

This is the generic import history for every non-WFO source. Leave `wfo_import` untouched.

| Column | Type | Notes |
|---|---|---|
| `id` | bigint identity | |
| `source` | text | e.g. `Wikidata` |
| `kind` | text | `Crosswalk` or `Details` |
| `started_at`, `completed_at` | timestamptz | |
| `status` | text | `Running`, `Succeeded`, `Failed`, `Cancelled`, or `Abandoned`. Same lifecycle as `wfo_import`, including detecting abandoned `Running` rows after acquiring the lock. |
| `parameters_json` | jsonb | CLI options used for the run |
| `rows_read`, `rows_inserted`, `rows_updated`, `rows_retired`, `warning_count` | bigint | |
| `validation_json` | jsonb | Summary report (section 4) |
| `error_message` | text | Sanitized; never includes request URLs or response bodies |

Imports for the same `source` serialize on a PostgreSQL advisory lock whose key is derived from the source name.

### `reference.wikidata_item`

| Column | Type | Notes |
|---|---|---|
| `id` | bigint identity | |
| `qid` | text, unique | e.g. `Q159657` |
| `taxon_name` | text null | P225 |
| `taxon_rank_qid` | text null | P105, as a QID |
| `label_en` | text null | English label. Often just the scientific name, but stored anyway. |
| `enwiki_title` | text null | English Wikipedia sitelink title |
| `image_file` | text null | First P18 value, as a Commons file name |
| `last_rev_id` | bigint null | Wikidata `lastrevid` from the last details fetch |
| `details_fetched_at` | timestamptz null | |
| `is_current` | bool | Set to false when the item no longer carries any WFO ID in a crosswalk run |
| `crosswalk_import_id`, `details_import_id` | bigint FK → `source_import`, null | |

### `reference.wikidata_wfo_link`

This is many-to-many: an item can carry several WFO IDs, and a WFO ID can appear on several items.

| Column | Type | Notes |
|---|---|---|
| `id` | bigint identity | |
| `qid` | text | FK by value to `wikidata_item.qid`. Use an actual FK on an `item_id` bigint column as well. |
| `wfo_id` | text | Raw value exactly as stored in Wikidata |
| `statement_rank` | text | `preferred` or `normal`. Deprecated-rank statements are not imported. |
| `resolved_wfo_id` | text null | After following WFO deduplication replacements with the existing `WfoIdResolver` |
| `wfo_taxon_id` | bigint FK → `wfo_taxon`, null | The resolved **current** taxon |
| `accepted_wfo_taxon_id` | bigint FK → `wfo_taxon`, null | Section 3.3 |
| `resolution_status` | text | `Resolved`, `NotFound`, `Cycle`, or `DepthLimit` (the resolver's statuses) |
| `is_current` | bool | |
| `import_id` | bigint FK → `source_import` | |

Unique on `(qid, wfo_id)`. Indexes on `wfo_id`, `wfo_taxon_id`, and `accepted_wfo_taxon_id`.

### `reference.wikidata_external_id`

| Column | Type | Notes |
|---|---|---|
| `item_id` | bigint FK | |
| `property` | text | One of: `P846` (GBIF taxon ID), `P961` (IPNI plant ID), `P1772` (USDA PLANTS ID), `P5037` (POWO ID) |
| `value` | text | Raw |

Unique on `(item_id, property, value)`, plus an index on `(property, value)`. Store all values of these properties, not just the first.

### `reference.wikidata_common_name`

| Column | Type | Notes |
|---|---|---|
| `item_id` | bigint FK | |
| `language` | text | The P1843 monolingual-text language code, e.g. `en`, `en-us`, `fr` |
| `name` | text | Raw |
| `normalized_name` | text | Section 3.4 |

Unique on `(item_id, language, name)`.

### Search indexes (same migration)

- `CREATE EXTENSION IF NOT EXISTS pg_trgm;` (via `HasPostgresExtension("pg_trgm")`).
- GIN `gin_trgm_ops` on `wfo_taxon.scientific_name`, `wfo_taxon.genus`, and `wikidata_common_name.normalized_name`.

This fixes the existing sequential scan in search.

---

## 2. Harvest phases

Add a new command group to `AnythingCanBeFarming.DataImport`:

```
dotnet run --project src/AnythingCanBeFarming.DataImport -- wikidata crosswalk
dotnet run --project src/AnythingCanBeFarming.DataImport -- wikidata details [--full] [--limit N]
dotnet run --project src/AnythingCanBeFarming.DataImport -- wikidata all        # crosswalk, then details
```

- Put the HTTP clients in the DataImport project (or a new small `AnythingCanBeFarming.Sources.Wikidata` library if that's cleaner). They're typed `HttpClient`s with a configurable base address so tests can stub them.
- **User-Agent:** `AnythingCanBeFarming/<version> (https://github.com/jjeandervin/anything-can-be-farming)`. Make it configurable via `Wikidata:UserAgent`.
- **No retries on 4xx other than 429.** Retry 429 and 503 up to 5 times, honoring `Retry-After` (default 10 s). Cancellation is honored everywhere.

### 2.1 Crosswalk (SPARQL)

- Endpoint: `https://query.wikidata.org/sparql`. Send a POST with form field `query` and header `Accept: application/sparql-results+json`.
- The query must be partitioned so no single request approaches the service's 60-second timeout. Partition by the last character of the WFO ID, which gives 10 queries:

```sparql
SELECT ?item ?wfo ?rank WHERE {
  ?item p:P7715 ?st .
  ?st ps:P7715 ?wfo ;
      wikibase:rank ?rank .
  FILTER(?rank != wikibase:DeprecatedRank)
  FILTER(STRENDS(?wfo, "7"))
}
```

- If a partition times out or errors, **subdivide it automatically** by two-character suffix (`"07"`, `"17"`, …) and retry each sub-partition once. Record partition timings in the validation JSON.
- Wait at least 2 seconds between SPARQL requests.
- Parse streaming. Extract the QID from the entity URI. Map rank URIs to `preferred` or `normal`.

**Publication** is one transaction per crosswalk run, using a temp staging table and binary COPY, like the WFO importer:

1. Upsert `wikidata_item` rows for the QIDs seen (sets `is_current = true`).
2. Upsert `wikidata_wfo_link` rows for the `(qid, wfo_id)` pairs seen. Update rank if it changed.
3. Links and items **not** seen in this run become `is_current = false`. Nothing is deleted.
4. Run WFO resolution (section 3.3) for all current links.
5. Write the `source_import` row and the validation JSON.

If the result set is empty, or less than 50% of the current link count, **fail publication**. That usually means an upstream outage, not real deletions. A `--allow-shrink` flag overrides this check.

### 2.2 Details (Action API)

- Endpoint: `https://www.wikidata.org/w/api.php`.
- Fetch in batches of 50 QIDs: `action=wbgetentities&ids=Q1|Q2|…&props=info|labels|claims|sitelinks&languages=en&sitefilter=enwiki&format=json&maxlag=5`.
  - Use `props=claims` to get P225, P105, P1843 (all languages), P18, P846, P961, P1772, P5037, and P7715.
  - Ignore deprecated-rank claims.
- **Incremental by default.** For current items, first request `props=info` in batches of 50 to get `lastrevid`. Fetch full entities only when the item was never fetched or its `lastrevid` changed. The `--full` flag skips this check and refetches everything. `--limit N` processes at most N items, for development.
- **Throttle:** one request in flight at a time, with at least 200 ms between requests.
  - A `maxlag` error response (`error.code == "maxlag"`) is retried after its `Retry-After`.
- **Missing or redirected items:** if the response shows the QID `missing`, or redirected to another QID, record a warning. A redirect's target is fetched and stored under the **target** QID. Mark the old item `is_current = false`.
- **Publication:** commit per batch of 500 items. This phase is long-running, so a crash resumes naturally through the incremental check. Each batch replaces that item's `wikidata_external_id` and `wikidata_common_name` rows wholesale, inside the batch transaction. The `source_import` row is updated at the end with totals.
- **Sanity check:** WFO IDs from P7715 in the details response are compared against the crosswalk's links. Mismatches are counted and reported as warnings; the crosswalk stays authoritative for links.

---

## 3. Rules

### 3.1 Property ID verification

Before the first harvest in each run, verify that each property ID used (P225, P105, P1843, P18, P846, P961, P1772, P5037, P7715) still has the expected datatype. Use `wbgetentities` on the property IDs themselves. Fail fast if a datatype changed.

### 3.2 QID and value hygiene

- QIDs must match `^Q[1-9][0-9]*$`.
- WFO IDs are stored raw. For resolution, a WFO ID that doesn't match `^wfo-[0-9]{10}$` gets status `NotFound` and a warning.

### 3.3 WFO resolution → accepted taxon

For each current link:

1. Run `WfoIdResolver.ResolveAsync(wfo_id)` to get `resolved_wfo_id`, `resolution_status`, and `wfo_taxon_id` (a current taxon only).
2. Set the accepted taxon:
   - If the resolved taxon's `TaxonomicStatus` is `Accepted`, `accepted_wfo_taxon_id` is the resolved taxon itself.
   - If it's `Synonym` with a non-null `AcceptedTaxonId` that points to a current taxon, use that.
   - Otherwise (`Unchecked`, a dangling synonym, or not resolved), leave it null.
3. Never infer links from names.

Resolution is **re-run for all links after every WFO backbone import too**. Add this as a step at the end of `wfo all`, and as `wikidata resolve` on its own, because backbone refreshes change current taxa.

### 3.4 Name normalization

`normalized_name`: Unicode NFKD, strip combining marks, lowercase with the invariant culture, replace `’` and `‘` with `'`, collapse whitespace runs to a single space, and trim.

Example: `"  Japanese  Maple "` becomes `"japanese maple"`.

Put this in one shared static helper with unit tests; later sources reuse it.

---

## 4. Validation report

The report is stored in `validation_json` and printed to the console.

**Crosswalk:**
- Partitions and their timings
- Rows read
- Distinct QIDs and distinct WFO IDs
- Items with more than one WFO ID; WFO IDs on more than one item (count, plus up to 20 examples)
- Links: inserted, updated, retired
- Resolution status distribution
- Links resolved to a current taxon
- Links with an accepted taxon
- Links redirected through deduplication
- **Coverage:** the percentage of current accepted WFO species (rank `species`) with at least one link

**Details:**
- Items checked, fetched, and unchanged
- Missing and redirected items
- Items with an English common name
- Common names by language (top 20 languages)
- Items with an enwiki title
- Items with an image
- External ID counts per property
- P7715 mismatch count
- Warnings

After the first real run, write `docs/wikidata-validation.md` with the observed counts, in the same style as `docs/wfo-validation.md`.

---

## 5. Reference search update

This is a breaking change to `GET /api/reference/plants/search`.

- `q` must be **2–200 characters** after trimming, otherwise 400.
- Match against three things:
  - `wfo_taxon.scientific_name` and `genus` (current taxa), using ILIKE backed by the trigram indexes
  - English common names: `language = 'en'` or starting with `en-`. Compare `normalized_name` against the normalized query.
- **One row per accepted taxon.** A match on a synonym or common name is reported against its accepted taxon (`accepted_wfo_taxon_id` for common names; `AcceptedTaxonId` for synonym taxa). Current taxa with no accepted taxon (`Unchecked`) appear as themselves.
- **Ranking**, highest first:
  1. exact match (scientific or common)
  2. prefix match
  3. word-prefix match (the query matches the start of any word)
  4. substring match

  Ties go to `Accepted` status first, then to lower rank depth (genus before species before infraspecific), then to the scientific name, then to `TaxonId`.
- **Response item:**

```jsonc
{
  "taxonId": "wfo-0000514950",
  "scientificName": "Acer palmatum",
  "scientificNameAuthorship": "Thunb.",
  "taxonRank": "species",
  "taxonomicStatus": "Accepted",
  "family": "Sapindaceae",
  "genus": "Acer",
  "commonName": "Japanese maple",       // best English common name, or null (section 5.1)
  "matchedOn": "commonName",            // "scientificName" | "synonym" | "commonName"
  "matchedText": "Japanese maple"       // the name that matched, for "you searched X" display
}
```

### 5.1 Best English common name

Pick the name with:
1. language exactly `en` over `en-*`
2. then the name that matched the query, if it did
3. then the shortest name
4. then ordinal order

Deterministic, no randomness.

### 5.2 Performance

On the full snapshot, a query like `maple` must return within 500 ms on a dev machine. Include `EXPLAIN (ANALYZE)` output for `maple`, `Japanese maple`, and `acer` in the validation doc, showing trigram index use.

---

## 6. Tests

Use stub HTTP handlers like `PlantNetClientTests`. **No test contacts Wikidata.** PostgreSQL tests use the existing `ACBF_TEST_POSTGRES` pattern.

**Unit tests:**
- SPARQL result parsing (QID extraction, rank mapping, deprecated excluded)
- Partition subdivision on timeout
- `Retry-After` handling
- `maxlag` handling
- `wbgetentities` parsing: monolingual text in several languages, multiple external IDs, deprecated claims ignored, missing and redirected entities, items without sitelinks or images
- Name normalization cases
- QID and WFO ID validation
- Best-common-name selection

**PostgreSQL tests:**
- Migration applies.
- Crosswalk publish, then a repeat run changes nothing.
- An upstream removal retires links without deleting them.
- The shrink guard fails publication, and `--allow-shrink` overrides it.
- Resolution through a deduplication chain, a synonym to its accepted taxon, and `Unchecked` to a null accepted taxon.
- Resolution re-run after a backbone refresh.
- Details incremental skip on unchanged `lastrevid`.
- Details replaces common names wholesale per item.
- A crash mid-details resumes correctly.

**Search tests:**
- A common-name exact match beats a substring match.
- A synonym match collapses into its accepted taxon.
- One row per accepted taxon.
- A 1-character `q` returns 400.
- Ranking tie-breaks are deterministic.

**Full-snapshot smoke test** (optional, using `ACBF_TEST_WFO_SNAPSHOT` with Wikidata imported):
- `Japanese maple` → `Acer palmatum` first.
- `red maple` → `Acer rubrum` in the top 3.
- `hosta` returns genus *Hosta* first.

---

## 7. README

Add a "Wikidata enrichment" section covering:
- Commands and flags
- Expected runtime, noting that a full details run is long and resumable
- The throttling and User-Agent policy
- CC0 licensing and the source-credit convention
- The search contract change

## 8. Acceptance checklist

- [ ] `wikidata all` completes against live Wikidata. A second run publishes no changes, and details reports all items unchanged.
- [ ] The validation doc is written with real counts and coverage.
- [ ] `Japanese maple`, `red maple`, `sugar maple`, `hosta`, and `black-eyed susan` each return the expected accepted taxon at or near the top.
- [ ] Search on the full snapshot meets the 500 ms target, with EXPLAIN output documented.
- [ ] `dotnet build` has no warnings, and `dotnet test` passes, including the PostgreSQL tests.

## 9. Suggested order

Commit after each step.

1. Migration and entities, including `source_import` and `pg_trgm`.
2. Name normalization helper and validation helpers, with tests.
3. SPARQL client and crosswalk publish, with tests.
4. Resolution step (`wikidata resolve`) and the hook into `wfo all`.
5. Details client, incremental logic, and publish, with tests.
6. Search update and tests.
7. Live run, validation doc, and README.
