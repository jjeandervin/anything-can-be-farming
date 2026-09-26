# Wikidata crosswalk and details validation

The first live Wikidata import ran on 2026-09-26 against an isolated PostgreSQL 17.9 database that already held the WFO 2026-09 backbone (1,664,014 current taxa) and its supplemental files (19,857 deduplication mappings). The `AddWikidataReference` migration applied in 15 seconds, including the three trigram indexes on existing data.

## Crosswalk

`wikidata crosswalk` completed in 5 min 10 s. All 11 SPARQL partitions succeeded on the first attempt, so none needed subdividing. The non-digit partition returned no rows: every WFO ID on Wikidata ends in a digit.

| Partition | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | non-digit |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Links | 92,447 | 92,429 | 92,156 | 92,012 | 91,999 | 92,256 | 92,185 | 92,639 | 92,578 | 92,372 | 0 |
| First run (s) | 40.0 | 11.0 | 9.1 | 12.2 | 37.2 | 11.7 | 24.0 | 21.5 | 11.8 | 9.9 | 9.0 |
| Rerun (s) | 13.6 | 13.1 | 10.3 | 10.0 | 10.9 | 12.1 | 11.4 | 10.5 | 13.4 | 12.4 | 13.1 |

The slowest partition took 40 s against the query service's 60 s limit.

| Crosswalk | Count |
| --- | ---: |
| Rows read (non-deprecated P7715 statements) | 923,073 |
| Duplicate statements collapsed | 0 |
| Links | 923,073 |
| Distinct QIDs | 922,791 |
| Distinct WFO IDs | 922,465 |
| Items with more than one WFO ID | 280 |
| WFO IDs on more than one item | 608 |
| Preferred / normal rank links | 0 / 923,073 |
| Links inserted / updated / retired | 923,073 / 0 / 0 |

Examples of items with several WFO IDs include Q134172 (`wfo-7000000572`, `wfo-7000000654`) and Q159973 (`wfo-0000510752`, `wfo-0000510753`). Up to 20 examples of each kind are stored in the run's validation JSON.

## Resolution

| Resolution | Count |
| --- | ---: |
| `Resolved` (current WFO taxon) | 921,700 |
| `NotFound` | 1,373 |
| `Cycle` / `DepthLimit` | 0 / 0 |
| Links redirected through WFO deduplication | 982 |
| Links with an accepted taxon | 870,937 |
| Resolved links to `Accepted` / `Synonym` / `Unchecked` taxa | 387,183 / 483,754 / 50,763 |
| Malformed WFO IDs | 1 |

More than half the resolved links point at WFO synonyms. Those are reported against their accepted taxon. The single malformed value is `wfo-1000051576-2026-06` (a release suffix appended to the ID), stored raw with status `NotFound` and one warning.

**Coverage:** 355,648 of 382,386 current accepted WFO species (rank `species`) have at least one link, or **93.01%**.

## Details

A 1,000-item `details --limit 1000` check ran first (15 s). The full incremental `wikidata details` then fetched the remaining 921,791 items in 3 h 49 min, in 1,846 committed batches of 500, at about 67 items per second, and completed without errors or warnings.

| Details | Count |
| --- | ---: |
| Items checked / fetched | 922,791 / 922,791 |
| Missing (deleted) items | 0 |
| Redirected items | 0 |
| P7715 mismatches against the crosswalk | 0 |
| Warnings | 0 |
| Items with an English common name (`en` or `en-*`) | 37,026 |
| Accepted or self-reporting taxa with an English common name | 35,358 |
| Items with an English Wikipedia title | 89,686 |
| Items with an image | 91,493 |

**Common names:** 278,072 across 346 language codes. English has 46,831 `en` names and 243 in `en-*` variants. The top 20 languages are:

| Language | Names |
| --- | ---: |
| zh | 81,773 |
| zh-hans | 73,542 |
| en | 46,831 |
| fi | 6,303 |
| de | 6,255 |
| nl | 5,652 |
| es | 5,249 |
| cs | 4,569 |
| fr | 4,461 |
| sl | 3,471 |
| zh-tw | 2,285 |
| ja | 2,120 |
| cy | 1,922 |
| ru | 1,792 |
| sv | 1,788 |
| pl | 1,562 |
| af | 1,411 |
| hsb | 999 |
| ko | 979 |
| et | 919 |

**External IDs:**

| Property | Values |
| --- | ---: |
| P846 (GBIF taxon ID) | 876,086 |
| P961 (IPNI plant ID) | 848,077 |
| P5037 (POWO ID) | 841,199 |
| P1772 (USDA PLANTS ID) | 40,664 |

Common names keep the source's spelling and case; several items carry case variants such as `black oak` and `Black Oak`, which share one normalized form. The Wikidata tables use about 1.3 GB including indexes. The trigram indexes are 91 MB (`wfo_taxon.ScientificName`), 37 MB (`wfo_taxon.Genus`), 19 MB (`wikidata_common_name.NormalizedName`), and 21 MB (`wikidata_common_name.CompactName`).

## Repeatability

A second `wikidata crosswalk` read the same 923,073 links and published nothing: 0 links or items inserted, updated, retired, or re-resolved.

A second incremental `wikidata details`, started right after the first finished, checked all 922,791 revisions in 2 h 34 min. 922,691 items were unchanged and fetched nothing. 100 items had a new `lastrevid` because they were edited on Wikidata between their first fetch and the recheck, up to about 6 hours apart. The run refetched and updated exactly those, inserting 2 name or external ID rows. They are spread across the whole item range, as expected for ordinary upstream edits. No items were missing or redirected, and there were no P7715 mismatches or warnings.

| Run | Duration | Items fetched | Items updated | Rows inserted / retired |
| --- | ---: | ---: | ---: | ---: |
| `details --limit 1000` | 0 h 00 min 15 s | 1,000 | 1,000 | 663 / 0 |
| `details` (first full) | 3 h 48 min 50 s | 921,791 | 921,791 | 2,883,435 / 0 |
| `details` (verification) | 2 h 33 min 34 s | 100 | 100 | 2 / 0 |

Revision checks are not much faster than full fetches, because each request's latency dominates. A routine incremental run therefore takes about 2½ hours.

## Search

Top results on the full snapshot:

| Query | First result | Matched on | Best English name |
| --- | --- | --- | --- |
| `Japanese maple` | *Acer palmatum* (species) | common name `Japanese maple` | Japanese maple |
| `red maple` | *Acer rubrum* (species) | common name `Red Maple` | Red Maple |
| `sugar maple` | *Acer saccharum* (species) | common name `sugar maple` | sugar maple |
| `hosta` | *Hosta* (genus) | scientific name | none (Wikidata has no English P1843 for the genus) |
| `black-eyed susan` | *Rudbeckia hirta* (species) | common name `blackeyed Susan`, ignoring the hyphen | blackeyed Susan |

Two findings from these queries changed the search:

- **Synonym ties.** WFO also uses "Hosta" as the name of a synonym genus, which points to *Cornutia*. Both were exact genus-rank matches, so *Cornutia* sorted first by name. Within a match tier, a taxon's own scientific or common name now beats a synonym match. *Cornutia* is second.
- **Hyphens.** Wikidata's only English name for *Rudbeckia hirta* is "blackeyed Susan", so `black-eyed susan` could not match under the spec's normalization. It only found *Thunbergia alata* ("black-eyed Susan vine"). Common names now also match with spaces and hyphens removed, using a generated `CompactName` column with its own trigram index (migration `AddCommonNameCompactName`, applied in 4 seconds). An exact compact match ranks just below a true exact match, and other compact-only matches rank last. `NormalizedName` still follows the spec.

**Performance.** With statistics refreshed after the load, the three required queries use the trigram indexes for scientific names, genera, normalized common names, and compact common names. They run far inside the 500 ms target:

| Query | Execution time (first / warm) |
| --- | ---: |
| `maple` | 92.5 / 2.6 ms |
| `Japanese maple` | 4.1 / 4.2 ms |
| `acer` | 681.5 / 23.7 ms |
| `black-eyed susan` | 13.6 / 3.0 ms |
| `ac` (two characters) | 4,377 / 1,899 ms |

The first `maple` and `acer` runs came right after the compact-name migration rewrote `wikidata_common_name`, so its pages were not cached. Before that migration, the first runs took 4.4 ms and 34.0 ms. Two-character queries cannot use trigram indexes and fall back to sequential scans. They are allowed by the contract but slow. The full-snapshot smoke test (`ACBF_TEST_WFO_SNAPSHOT`) checks the maple, hosta, and black-eyed Susan results and times `maple` through the API.

Plans below come from `EXPLAIN (ANALYZE, BUFFERS, COSTS OFF)` on the endpoint's SQL with a page size of 20, warm cache. The rank-depth `CASE` expression in the sort key is abbreviated.

### `maple`

```text
 Nested Loop Left Join (actual time=1.983..2.347 rows=20 loops=1)
   Buffers: shared hit=1587
   ->  Limit (actual time=1.909..1.913 rows=20 loops=1)
         Buffers: shared hit=1162
         ->  Sort (actual time=1.908..1.911 rows=20 loops=1)
               Sort Key: "*SELECT* 1".tier, (("*SELECT* 1".matched_on = 'synonym'::text)), ((t."TaxonomicStatus" IS DISTINCT FROM 'Accepted'::text)), (rank depth CASE ... END), t."ScientificName" COLLATE "C", t."TaxonId" COLLATE "C"
               Sort Method: top-N heapsort  Memory: 32kB
               Buffers: shared hit=1162
               ->  Nested Loop (actual time=1.476..1.822 rows=95 loops=1)
                     Buffers: shared hit=1151
                     ->  Unique (actual time=1.434..1.453 rows=95 loops=1)
                           Buffers: shared hit=771
                           ->  Sort (actual time=1.433..1.441 rows=159 loops=1)
                                 Sort Key: "*SELECT* 1".target_id, "*SELECT* 1".tier, "*SELECT* 1".source_order, (length("*SELECT* 1".matched_text)), "*SELECT* 1".matched_text COLLATE "C"
                                 Sort Method: quicksort  Memory: 37kB
                                 Buffers: shared hit=771
                                 ->  Result (actual time=0.476..1.376 rows=159 loops=1)
                                       Buffers: shared hit=768
                                       ->  Append (actual time=0.471..1.351 rows=159 loops=1)
                                             Buffers: shared hit=768
                                             ->  Subquery Scan on "*SELECT* 1" (actual time=0.471..0.505 rows=8 loops=1)
                                                   Buffers: shared hit=66
                                                   ->  Nested Loop Left Join (actual time=0.470..0.504 rows=8 loops=1)
                                                         Filter: (CASE WHEN ((t_1."TaxonomicStatus" = 'Synonym'::text) AND (a."Id" IS NOT NULL)) THEN a."Id" ELSE t_1."Id" END IS NOT NULL)
                                                         Buffers: shared hit=66
                                                         ->  Bitmap Heap Scan on wfo_taxon t_1 (actual time=0.452..0.465 rows=8 loops=1)
                                                               Recheck Cond: (("ScientificName" ~~* '%maple%'::text) OR ("Genus" ~~* '%maple%'::text))
                                                               Filter: "IsCurrent"
                                                               Heap Blocks: exact=8
                                                               Buffers: shared hit=46
                                                               ->  BitmapOr (actual time=0.440..0.440 rows=0 loops=1)
                                                                     Buffers: shared hit=38
                                                                     ->  Bitmap Index Scan on "IX_wfo_taxon_ScientificName_trgm" (actual time=0.255..0.255 rows=8 loops=1)
                                                                           Index Cond: ("ScientificName" ~~* '%maple%'::text)
                                                                           Buffers: shared hit=21
                                                                     ->  Bitmap Index Scan on "IX_wfo_taxon_Genus_trgm" (actual time=0.185..0.185 rows=3 loops=1)
                                                                           Index Cond: ("Genus" ~~* '%maple%'::text)
                                                                           Buffers: shared hit=17
                                                         ->  Index Scan using "PK_wfo_taxon" on wfo_taxon a (actual time=0.003..0.003 rows=1 loops=8)
                                                               Index Cond: ("Id" = t_1."AcceptedTaxonId")
                                                               Filter: "IsCurrent"
                                                               Buffers: shared hit=20
                                             ->  Subquery Scan on "*SELECT* 2" (actual time=0.127..0.836 rows=151 loops=1)
                                                   Buffers: shared hit=702
                                                   ->  Nested Loop (actual time=0.126..0.825 rows=151 loops=1)
                                                         Buffers: shared hit=702
                                                         ->  Bitmap Heap Scan on wikidata_common_name n (actual time=0.115..0.390 rows=151 loops=1)
                                                               Recheck Cond: (("NormalizedName" ~~ '%maple%'::text) OR ("CompactName" ~~ '%maple%'::text))
                                                               Filter: (("Language" = 'en'::text) OR ("Language" ~~ 'en-%'::text))
                                                               Heap Blocks: exact=78
                                                               Buffers: shared hit=98
                                                               ->  BitmapOr (actual time=0.107..0.107 rows=0 loops=1)
                                                                     Buffers: shared hit=20
                                                                     ->  Bitmap Index Scan on "IX_wikidata_common_name_NormalizedName_trgm" (actual time=0.051..0.051 rows=151 loops=1)
                                                                           Index Cond: ("NormalizedName" ~~ '%maple%'::text)
                                                                           Buffers: shared hit=10
                                                                     ->  Bitmap Index Scan on "IX_wikidata_common_name_CompactName_trgm" (actual time=0.056..0.056 rows=151 loops=1)
                                                                           Index Cond: ("CompactName" ~~ '%maple%'::text)
                                                                           Buffers: shared hit=10
                                                         ->  Index Scan using "IX_wikidata_wfo_link_ItemId" on wikidata_wfo_link l (actual time=0.002..0.003 rows=1 loops=151)
                                                               Index Cond: ("ItemId" = n."ItemId")
                                                               Filter: ("IsCurrent" AND (COALESCE("AcceptedWfoTaxonId", "WfoTaxonId") IS NOT NULL))
                                                               Buffers: shared hit=604
                     ->  Index Scan using "PK_wfo_taxon" on wfo_taxon t (actual time=0.003..0.003 rows=1 loops=95)
                           Index Cond: ("Id" = "*SELECT* 1".target_id)
                           Filter: "IsCurrent"
                           Buffers: shared hit=380
   ->  Limit (actual time=0.021..0.021 rows=1 loops=20)
         Buffers: shared hit=425
         ->  Sort (actual time=0.021..0.021 rows=1 loops=20)
               Sort Key: ((n_1."Language" <> 'en'::text)), (((n_1."NormalizedName" !~~ '%maple%'::text) AND (n_1."CompactName" !~~ '%maple%'::text))), (length(n_1."Name")), n_1."Name" COLLATE "C"
               Sort Method: quicksort  Memory: 25kB
               Buffers: shared hit=425
               ->  Nested Loop (actual time=0.015..0.020 rows=2 loops=20)
                     Buffers: shared hit=425
                     ->  Bitmap Heap Scan on wikidata_wfo_link l_1 (actual time=0.007..0.009 rows=3 loops=20)
                           Recheck Cond: (("AcceptedWfoTaxonId" = t."Id") OR ("WfoTaxonId" = t."Id"))
                           Filter: ("IsCurrent" AND (("AcceptedWfoTaxonId" = t."Id") OR (("AcceptedWfoTaxonId" IS NULL) AND ("WfoTaxonId" = t."Id"))))
                           Heap Blocks: exact=67
                           Buffers: shared hit=187
                           ->  BitmapOr (actual time=0.006..0.006 rows=0 loops=20)
                                 Buffers: shared hit=120
                                 ->  Bitmap Index Scan on "IX_wikidata_wfo_link_AcceptedWfoTaxonId" (actual time=0.003..0.003 rows=3 loops=20)
                                       Index Cond: ("AcceptedWfoTaxonId" = t."Id")
                                       Buffers: shared hit=60
                                 ->  Bitmap Index Scan on "IX_wikidata_wfo_link_WfoTaxonId" (actual time=0.002..0.002 rows=1 loops=20)
                                       Index Cond: ("WfoTaxonId" = t."Id")
                                       Buffers: shared hit=60
                     ->  Index Scan using "IX_wikidata_common_name_ItemId_Language_Name" on wikidata_common_name n_1 (actual time=0.003..0.003 rows=0 loops=67)
                           Index Cond: ("ItemId" = l_1."ItemId")
                           Filter: (("Language" = 'en'::text) OR ("Language" ~~ 'en-%'::text))
                           Rows Removed by Filter: 1
                           Buffers: shared hit=238
 Planning:
   Buffers: shared hit=694
 Planning Time: 2.750 ms
 Execution Time: 2.610 ms
```

### `Japanese maple`

```text
 Nested Loop Left Join (actual time=3.885..3.889 rows=1 loops=1)
   Buffers: shared hit=455
   ->  Limit (actual time=3.750..3.752 rows=1 loops=1)
         Buffers: shared hit=387
         ->  Sort (actual time=3.749..3.751 rows=1 loops=1)
               Sort Key: "*SELECT* 1".tier, (("*SELECT* 1".matched_on = 'synonym'::text)), ((t."TaxonomicStatus" IS DISTINCT FROM 'Accepted'::text)), (rank depth CASE ... END), t."ScientificName" COLLATE "C", t."TaxonId" COLLATE "C"
               Sort Method: quicksort  Memory: 25kB
               Buffers: shared hit=387
               ->  Nested Loop (actual time=3.718..3.721 rows=1 loops=1)
                     Buffers: shared hit=376
                     ->  Unique (actual time=3.635..3.637 rows=1 loops=1)
                           Buffers: shared hit=372
                           ->  Sort (actual time=3.634..3.637 rows=1 loops=1)
                                 Sort Key: "*SELECT* 1".target_id, "*SELECT* 1".tier, "*SELECT* 1".source_order, (length("*SELECT* 1".matched_text)), "*SELECT* 1".matched_text COLLATE "C"
                                 Sort Method: quicksort  Memory: 25kB
                                 Buffers: shared hit=372
                                 ->  Result (actual time=3.608..3.612 rows=1 loops=1)
                                       Buffers: shared hit=369
                                       ->  Append (actual time=3.603..3.606 rows=1 loops=1)
                                             Buffers: shared hit=369
                                             ->  Subquery Scan on "*SELECT* 1" (actual time=3.102..3.103 rows=0 loops=1)
                                                   Buffers: shared hit=285
                                                   ->  Nested Loop Left Join (actual time=3.101..3.102 rows=0 loops=1)
                                                         Filter: (CASE WHEN ((t_1."TaxonomicStatus" = 'Synonym'::text) AND (a."Id" IS NOT NULL)) THEN a."Id" ELSE t_1."Id" END IS NOT NULL)
                                                         Buffers: shared hit=285
                                                         ->  Bitmap Heap Scan on wfo_taxon t_1 (actual time=3.101..3.101 rows=0 loops=1)
                                                               Recheck Cond: (("ScientificName" ~~* '%Japanese maple%'::text) OR ("Genus" ~~* '%Japanese maple%'::text))
                                                               Filter: "IsCurrent"
                                                               Buffers: shared hit=285
                                                               ->  BitmapOr (actual time=3.097..3.097 rows=0 loops=1)
                                                                     Buffers: shared hit=285
                                                                     ->  Bitmap Index Scan on "IX_wfo_taxon_ScientificName_trgm" (actual time=2.601..2.601 rows=0 loops=1)
                                                                           Index Cond: ("ScientificName" ~~* '%Japanese maple%'::text)
                                                                           Buffers: shared hit=222
                                                                     ->  Bitmap Index Scan on "IX_wfo_taxon_Genus_trgm" (actual time=0.495..0.495 rows=0 loops=1)
                                                                           Index Cond: ("Genus" ~~* '%Japanese maple%'::text)
                                                                           Buffers: shared hit=63
                                                         ->  Index Scan using "PK_wfo_taxon" on wfo_taxon a (never executed)
                                                               Index Cond: ("Id" = t_1."AcceptedTaxonId")
                                                               Filter: "IsCurrent"
                                             ->  Subquery Scan on "*SELECT* 2" (actual time=0.500..0.502 rows=1 loops=1)
                                                   Buffers: shared hit=84
                                                   ->  Nested Loop (actual time=0.500..0.501 rows=1 loops=1)
                                                         Buffers: shared hit=84
                                                         ->  Bitmap Heap Scan on wikidata_common_name n (actual time=0.485..0.486 rows=1 loops=1)
                                                               Recheck Cond: (("NormalizedName" ~~ '%japanese maple%'::text) OR ("CompactName" ~~ '%japanesemaple%'::text))
                                                               Filter: (("Language" = 'en'::text) OR ("Language" ~~ 'en-%'::text))
                                                               Heap Blocks: exact=1
                                                               Buffers: shared hit=80
                                                               ->  BitmapOr (actual time=0.472..0.472 rows=0 loops=1)
                                                                     Buffers: shared hit=79
                                                                     ->  Bitmap Index Scan on "IX_wikidata_common_name_NormalizedName_trgm" (actual time=0.287..0.287 rows=1 loops=1)
                                                                           Index Cond: ("NormalizedName" ~~ '%japanese maple%'::text)
                                                                           Buffers: shared hit=45
                                                                     ->  Bitmap Index Scan on "IX_wikidata_common_name_CompactName_trgm" (actual time=0.185..0.185 rows=1 loops=1)
                                                                           Index Cond: ("CompactName" ~~ '%japanesemaple%'::text)
                                                                           Buffers: shared hit=34
                                                         ->  Index Scan using "IX_wikidata_wfo_link_ItemId" on wikidata_wfo_link l (actual time=0.012..0.012 rows=1 loops=1)
                                                               Index Cond: ("ItemId" = n."ItemId")
                                                               Filter: ("IsCurrent" AND (COALESCE("AcceptedWfoTaxonId", "WfoTaxonId") IS NOT NULL))
                                                               Buffers: shared hit=4
                     ->  Index Scan using "PK_wfo_taxon" on wfo_taxon t (actual time=0.050..0.050 rows=1 loops=1)
                           Index Cond: ("Id" = "*SELECT* 1".target_id)
                           Filter: "IsCurrent"
                           Buffers: shared hit=4
   ->  Limit (actual time=0.134..0.134 rows=1 loops=1)
         Buffers: shared hit=68
         ->  Sort (actual time=0.133..0.133 rows=1 loops=1)
               Sort Key: ((n_1."Language" <> 'en'::text)), (((n_1."NormalizedName" !~~ '%japanese maple%'::text) AND (n_1."CompactName" !~~ '%japanesemaple%'::text))), (length(n_1."Name")), n_1."Name" COLLATE "C"
               Sort Method: quicksort  Memory: 25kB
               Buffers: shared hit=68
               ->  Nested Loop (actual time=0.087..0.126 rows=1 loops=1)
                     Buffers: shared hit=68
                     ->  Bitmap Heap Scan on wikidata_wfo_link l_1 (actual time=0.029..0.064 rows=15 loops=1)
                           Recheck Cond: (("AcceptedWfoTaxonId" = t."Id") OR ("WfoTaxonId" = t."Id"))
                           Filter: ("IsCurrent" AND (("AcceptedWfoTaxonId" = t."Id") OR (("AcceptedWfoTaxonId" IS NULL) AND ("WfoTaxonId" = t."Id"))))
                           Heap Blocks: exact=15
                           Buffers: shared hit=21
                           ->  BitmapOr (actual time=0.020..0.020 rows=0 loops=1)
                                 Buffers: shared hit=6
                                 ->  Bitmap Index Scan on "IX_wikidata_wfo_link_AcceptedWfoTaxonId" (actual time=0.011..0.012 rows=15 loops=1)
                                       Index Cond: ("AcceptedWfoTaxonId" = t."Id")
                                       Buffers: shared hit=3
                                 ->  Bitmap Index Scan on "IX_wikidata_wfo_link_WfoTaxonId" (actual time=0.008..0.008 rows=1 loops=1)
                                       Index Cond: ("WfoTaxonId" = t."Id")
                                       Buffers: shared hit=3
                     ->  Index Scan using "IX_wikidata_common_name_ItemId_Language_Name" on wikidata_common_name n_1 (actual time=0.004..0.004 rows=0 loops=15)
                           Index Cond: ("ItemId" = l_1."ItemId")
                           Filter: (("Language" = 'en'::text) OR ("Language" ~~ 'en-%'::text))
                           Rows Removed by Filter: 0
                           Buffers: shared hit=47
 Planning:
   Buffers: shared hit=694
 Planning Time: 2.710 ms
 Execution Time: 4.185 ms
```

### `acer`

```text
 Nested Loop Left Join (actual time=22.886..23.219 rows=20 loops=1)
   Buffers: shared hit=19583
   ->  Limit (actual time=22.805..22.809 rows=20 loops=1)
         Buffers: shared hit=19111
         ->  Sort (actual time=22.804..22.807 rows=20 loops=1)
               Sort Key: "*SELECT* 1".tier, (("*SELECT* 1".matched_on = 'synonym'::text)), ((t."TaxonomicStatus" IS DISTINCT FROM 'Accepted'::text)), (rank depth CASE ... END), t."ScientificName" COLLATE "C", t."TaxonId" COLLATE "C"
               Sort Method: top-N heapsort  Memory: 34kB
               Buffers: shared hit=19111
               ->  Nested Loop (actual time=18.506..22.202 rows=1469 loops=1)
                     Buffers: shared hit=19100
                     ->  Unique (actual time=18.464..18.864 rows=1469 loops=1)
                           Buffers: shared hit=13224
                           ->  Sort (actual time=18.463..18.619 rows=3858 loops=1)
                                 Sort Key: "*SELECT* 1".target_id, "*SELECT* 1".tier, "*SELECT* 1".source_order, (length("*SELECT* 1".matched_text)), "*SELECT* 1".matched_text COLLATE "C"
                                 Sort Method: quicksort  Memory: 483kB
                                 Buffers: shared hit=13224
                                 ->  Result (actual time=2.128..16.856 rows=3858 loops=1)
                                       Buffers: shared hit=13221
                                       ->  Append (actual time=2.123..16.346 rows=3858 loops=1)
                                             Buffers: shared hit=13221
                                             ->  Subquery Scan on "*SELECT* 1" (actual time=2.123..15.866 rows=3854 loops=1)
                                                   Buffers: shared hit=13136
                                                   ->  Nested Loop Left Join (actual time=2.122..15.580 rows=3854 loops=1)
                                                         Filter: (CASE WHEN ((t_1."TaxonomicStatus" = 'Synonym'::text) AND (a."Id" IS NOT NULL)) THEN a."Id" ELSE t_1."Id" END IS NOT NULL)
                                                         Buffers: shared hit=13136
                                                         ->  Bitmap Heap Scan on wfo_taxon t_1 (actual time=2.101..7.543 rows=3854 loops=1)
                                                               Recheck Cond: (("ScientificName" ~~* '%acer%'::text) OR ("Genus" ~~* '%acer%'::text))
                                                               Rows Removed by Index Recheck: 171
                                                               Filter: "IsCurrent"
                                                               Heap Blocks: exact=1833
                                                               Buffers: shared hit=1876
                                                               ->  BitmapOr (actual time=1.980..1.980 rows=0 loops=1)
                                                                     Buffers: shared hit=43
                                                                     ->  Bitmap Index Scan on "IX_wfo_taxon_ScientificName_trgm" (actual time=1.641..1.641 rows=4025 loops=1)
                                                                           Index Cond: ("ScientificName" ~~* '%acer%'::text)
                                                                           Buffers: shared hit=27
                                                                     ->  Bitmap Index Scan on "IX_wfo_taxon_Genus_trgm" (actual time=0.338..0.338 rows=2527 loops=1)
                                                                           Index Cond: ("Genus" ~~* '%acer%'::text)
                                                                           Buffers: shared hit=16
                                                         ->  Index Scan using "PK_wfo_taxon" on wfo_taxon a (actual time=0.001..0.001 rows=1 loops=3854)
                                                               Index Cond: ("Id" = t_1."AcceptedTaxonId")
                                                               Filter: "IsCurrent"
                                                               Buffers: shared hit=11260
                                             ->  Subquery Scan on "*SELECT* 2" (actual time=0.160..0.253 rows=4 loops=1)
                                                   Buffers: shared hit=85
                                                   ->  Nested Loop (actual time=0.160..0.253 rows=4 loops=1)
                                                         Buffers: shared hit=85
                                                         ->  Bitmap Heap Scan on wikidata_common_name n (actual time=0.150..0.235 rows=4 loops=1)
                                                               Recheck Cond: (("NormalizedName" ~~ '%acer%'::text) OR ("CompactName" ~~ '%acer%'::text))
                                                               Rows Removed by Index Recheck: 6
                                                               Filter: (("Language" = 'en'::text) OR ("Language" ~~ 'en-%'::text))
                                                               Rows Removed by Filter: 60
                                                               Heap Blocks: exact=55
                                                               Buffers: shared hit=69
                                                               ->  BitmapOr (actual time=0.124..0.125 rows=0 loops=1)
                                                                     Buffers: shared hit=14
                                                                     ->  Bitmap Index Scan on "IX_wikidata_common_name_NormalizedName_trgm" (actual time=0.049..0.049 rows=40 loops=1)
                                                                           Index Cond: ("NormalizedName" ~~ '%acer%'::text)
                                                                           Buffers: shared hit=7
                                                                     ->  Bitmap Index Scan on "IX_wikidata_common_name_CompactName_trgm" (actual time=0.074..0.075 rows=70 loops=1)
                                                                           Index Cond: ("CompactName" ~~ '%acer%'::text)
                                                                           Buffers: shared hit=7
                                                         ->  Index Scan using "IX_wikidata_wfo_link_ItemId" on wikidata_wfo_link l (actual time=0.003..0.004 rows=1 loops=4)
                                                               Index Cond: ("ItemId" = n."ItemId")
                                                               Filter: ("IsCurrent" AND (COALESCE("AcceptedWfoTaxonId", "WfoTaxonId") IS NOT NULL))
                                                               Buffers: shared hit=16
                     ->  Index Scan using "PK_wfo_taxon" on wfo_taxon t (actual time=0.001..0.001 rows=1 loops=1469)
                           Index Cond: ("Id" = "*SELECT* 1".target_id)
                           Filter: "IsCurrent"
                           Buffers: shared hit=5876
   ->  Limit (actual time=0.020..0.020 rows=0 loops=20)
         Buffers: shared hit=472
         ->  Sort (actual time=0.020..0.020 rows=0 loops=20)
               Sort Key: ((n_1."Language" <> 'en'::text)), (((n_1."NormalizedName" !~~ '%acer%'::text) AND (n_1."CompactName" !~~ '%acer%'::text))), (length(n_1."Name")), n_1."Name" COLLATE "C"
               Sort Method: quicksort  Memory: 25kB
               Buffers: shared hit=472
               ->  Nested Loop (actual time=0.015..0.018 rows=1 loops=20)
                     Buffers: shared hit=472
                     ->  Bitmap Heap Scan on wikidata_wfo_link l_1 (actual time=0.005..0.008 rows=4 loops=20)
                           Recheck Cond: (("AcceptedWfoTaxonId" = t."Id") OR ("WfoTaxonId" = t."Id"))
                           Filter: ("IsCurrent" AND (("AcceptedWfoTaxonId" = t."Id") OR (("AcceptedWfoTaxonId" IS NULL) AND ("WfoTaxonId" = t."Id"))))
                           Heap Blocks: exact=80
                           Buffers: shared hit=200
                           ->  BitmapOr (actual time=0.003..0.003 rows=0 loops=20)
                                 Buffers: shared hit=120
                                 ->  Bitmap Index Scan on "IX_wikidata_wfo_link_AcceptedWfoTaxonId" (actual time=0.002..0.002 rows=4 loops=20)
                                       Index Cond: ("AcceptedWfoTaxonId" = t."Id")
                                       Buffers: shared hit=60
                                 ->  Bitmap Index Scan on "IX_wikidata_wfo_link_WfoTaxonId" (actual time=0.001..0.001 rows=1 loops=20)
                                       Index Cond: ("WfoTaxonId" = t."Id")
                                       Buffers: shared hit=60
                     ->  Index Scan using "IX_wikidata_common_name_ItemId_Language_Name" on wikidata_common_name n_1 (actual time=0.002..0.002 rows=0 loops=80)
                           Index Cond: ("ItemId" = l_1."ItemId")
                           Filter: (("Language" = 'en'::text) OR ("Language" ~~ 'en-%'::text))
                           Rows Removed by Filter: 2
                           Buffers: shared hit=272
 Planning:
   Buffers: shared hit=694
 Planning Time: 3.555 ms
 Execution Time: 23.746 ms
```

## Test runs

All 182 tests passed with both `ACBF_TEST_POSTGRES` and `ACBF_TEST_WFO_SNAPSHOT` set, including the full-snapshot WFO and Wikidata smoke tests. No test contacts Wikidata. Console logs of these runs were kept outside the repository.
