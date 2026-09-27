# Spec: Wikipedia lead descriptions

## Goal

For every current Wikidata item that has an English Wikipedia sitelink (about 90k), stage:
- the article's **lead section as plain text**
- Wikipedia's **short description**
- enough metadata to attribute the text, license it, refresh it, and trace it

This is the first description source. Staging only: choosing which description a plant shows belongs to the catalog spec.

## Dependencies

This builds on the Wikidata spec. It reuses:
- `reference.source_import`: lifecycle, advisory lock, and status handling
- `wikidata_item.enwiki_title`: the only way articles are selected; never search Wikipedia by name
- the HTTP conventions: User-Agent, throttling, `maxlag`, and `Retry-After` handling

## Out of scope

- Article bodies, including "Cultivation" sections. A later spec covers those.
- Images, infoboxes, and taxoboxes.
- Choosing a description per WFO taxon, and any UI.

## Licensing

Wikipedia text is **CC BY-SA 4.0**. Store the attribution with every row:
- the article URL
- the revision ID (which points to the author history)
- the license name and URL
- the retrieval time

Store the text **unmodified**. Normalization happens only in separate derived columns.

---

## 1. Schema (migration `AddWikipediaReference`)

### `reference.wikipedia_article`

| Column | Type | Notes |
|---|---|---|
| `id` | bigint identity | |
| `wiki` | text | `enwiki`. Keeps other languages possible later. |
| `requested_title` | text | The sitelink title as stored on the Wikidata item |
| `title` | text null | The title after normalization and redirects |
| `page_id` | bigint null | |
| `last_rev_id` | bigint null | |
| `wikibase_item` | text null | QID from `pageprops.wikibase_item`. Must match the item that requested it; see §3. |
| `short_description` | text null | `pageprops.wikibase-shortdesc` |
| `lead_text` | text null | Plain-text lead section, raw from TextExtracts |
| `lead_chars` | int null | Length of `lead_text`, for quick quality queries |
| `status` | text | `Ok`, `Missing`, `Disambiguation`, `ItemMismatch`, `EmptyLead`, or `Redirected` (the last is informational; `title` holds the target) |
| `url` | text null | `https://en.wikipedia.org/wiki/{title with spaces → underscores, percent-encoded}` |
| `license` | text | `CC BY-SA 4.0` |
| `license_url` | text | `https://creativecommons.org/licenses/by-sa/4.0/` |
| `fetched_at` | timestamptz null | |
| `is_current` | bool | False when no current Wikidata item carries this sitelink anymore |
| `import_id` | bigint FK → `source_import` | |

Unique on `(wiki, requested_title)`. Index on `wikibase_item` and `status`.

### `reference.wikipedia_item_article`

Links Wikidata items to articles. Normally one-to-one, but the sitelink comes from Wikidata, so model it explicitly.

| Column | Type |
|---|---|
| `item_id` | bigint FK → `wikidata_item` |
| `article_id` | bigint FK → `wikipedia_article` |

Unique on `(item_id, article_id)`.

---

## 2. Command

```
dotnet run --project src/AnythingCanBeFarming.DataImport -- wikipedia leads [--full] [--limit N]
```

This also runs as the last step of `wikidata all` when invoked as `wikidata all --with-wikipedia`. Keep it off by default.

### 2.1 Selecting titles

Select `enwiki_title` from current `wikidata_item` rows where `enwiki_title` is not null. Upsert `wikipedia_article` and `wikipedia_item_article` rows for new titles. Titles no longer referenced by any current item become `is_current = false`; nothing is deleted.

### 2.2 Revision check (incremental)

- Endpoint: `https://en.wikipedia.org/w/api.php`.
- Request, in batches of **50** titles:

  `action=query&prop=info|pageprops&ppprop=wikibase_item|wikibase-shortdesc|disambiguation&redirects=1&titles=A|B|…&format=json&formatversion=2&maxlag=5`

- Map each response back to its requested title using the `normalized` and `redirects` arrays in the response. The API reorders and renames titles, so never rely on position.
- An article needs its lead fetched if it has never been fetched, if `lastrevid` changed, or if `--full` is set.
- Store `short_description`, `wikibase_item`, the disambiguation flag, `page_id`, and `title` from this step for **every** article, changed or not. They're cheap and keep the metadata fresh.

### 2.3 Lead fetch

- Request, in batches of **20** titles (the TextExtracts limit for intro extracts):

  `action=query&prop=extracts|info&exintro=1&explaintext=1&exsectionformat=plain&exlimit=20&redirects=1&titles=…&format=json&formatversion=2&maxlag=5`

- If the response contains `continue`, follow it until the batch is complete. TextExtracts can return fewer extracts than requested.
- Store `extract` exactly as returned in `lead_text`, and record `lastrevid` from the same response. If the revision moved between the check and the fetch, store what was fetched; the next run catches up.

### 2.4 Politeness and publishing

- Same User-Agent as Wikidata, one request in flight, at least 200 ms between requests, and `maxlag=5` with `Retry-After` handling.
- Commit per 500 articles, as the Wikidata details phase does. A crash resumes through the incremental check.
- The `source_import` row gets source `Wikipedia`, kind `Leads`, and the counts from §4.

---

## 3. Status rules

Evaluate in this order:

1. The page doesn't exist → `Missing`. Keep the previous text but mark `is_current = false` only through §2.1, never here.
2. `pageprops.disambiguation` is present → `Disambiguation`. Store no lead.
3. `wikibase_item` differs from the requesting item's QID → `ItemMismatch`. Store the lead anyway for inspection, but the catalog must not use it. This catches sitelinks that point at the wrong article or at a redirect to a broader topic.
4. `lead_text` is empty or whitespace → `EmptyLead`.
5. Otherwise → `Ok`. If a redirect was followed, also record the target in `title`. The redirect doesn't change the status, but it should be reported.

A redirect whose target resolves to the same `wikibase_item` is fine (`Ok`). A redirect to a page about a different item becomes `ItemMismatch` by rule 3. This matters: many species redirect to their genus article.

---

## 4. Validation report

- Titles selected, checked, fetched, and unchanged
- Status distribution
- Redirects followed, and how many landed on a different item (the `ItemMismatch` count)
- Lead length distribution: min, p10, median, p90, max
- Count of **stub-like** leads: under 200 characters, or a single sentence matching `^[^.]+ is a (species|genus) of`
- **By rank**, using the linked WFO taxon: `Ok` articles for species, genus, and infraspecific taxa
- **Garden check:** the lead's first 150 characters and its status for ACSA3's taxon (*Acer saccharum*), *Acer palmatum*, *Rudbeckia hirta*, *Echinacea purpurea*, *Hosta* (the genus), and *Cornus florida*. Resolve each through the WFO → Wikidata link, not by title.

Write `docs/wikipedia-validation.md` after the first real run, in the style of the other validation docs.

## 5. Tests

- **Unit:**
  - response mapping with `normalized` and `redirects` arrays in both directions
  - partial extract batches with `continue`
  - disambiguation, missing pages, and item mismatches
  - URL encoding, e.g. titles containing `'`, `×`, `(`, and `/`
  - the status precedence order
- **PostgreSQL:**
  - first run, then a repeat run that fetches nothing when revisions are unchanged
  - a changed revision triggers exactly one refetch
  - a removed sitelink retires the article
  - crash and resume
  - the `--full` flag
- **Fixtures:** capture one real response of each request type from the live API and save it under `tests/.../Fixtures/wikipedia/`. No test contacts Wikipedia.

## 6. README

Add a "Wikipedia descriptions" section covering:
- the command and its flags
- the expected runtime
- the CC BY-SA 4.0 obligations: show attribution with a link and the license wherever the text appears, and don't alter the stored text
- the status meanings

## 7. Acceptance

- [ ] The full run completes. A second run fetches only changed revisions.
- [ ] The validation doc is written, including the garden check and stub counts.
- [ ] At least 95% of selected titles end `Ok`; the rest are explained by status.
- [ ] `dotnet build` produces no warnings, and `dotnet test` passes.

## 8. Order

1. Migration and entities
2. API client, response mapping, and fixtures, with unit tests
3. Revision check, lead fetch, and publish, with PostgreSQL tests
4. Live run, validation doc, and README
