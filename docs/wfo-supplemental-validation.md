# WFO supplemental identifier validation

The three staged CSV files were imported into the existing local `acbf_wfo_validation` PostgreSQL database after applying `AddWfoSupplemental`. Its 1,664,014-row backbone was already populated; no backbone reimport was needed. The release association is `2026-09`, taken from the package's `backbone/fileInfo.json`, not inferred from the source modification timestamps.

## Inspected source layout

An independent streaming CSV scan checked every record:

| File | Header fields | Data fields | Rows |
| --- | ---: | ---: | ---: |
| `015_ipni_to_wfo.csv` | 2 | 2 | 1,684,056 |
| `040_deprecated_names_lookup.csv` | 5 | 5 | 43,955 |
| `050_deduplicated_ids_lookup.csv` | 5 | 6 | 19,857 |

The deduplication header is:

```text
wfo_id,name_canonical,authors_string,rank,nomenclatural_status
```

The first data records are:

```text
wfo-4000048768,wfo-4000048766,?,,genus,deprecated
wfo-4000048771,wfo-4000048766,?,,genus,deprecated
wfo-4000048776,wfo-4000048766,?,,genus,deprecated
```

The missing header is interpreted as `replacement_wfo_id` in position 2. All data rows use deprecated ID, replacement ID, canonical name, authors, rank, status. The importer recognizes this exact source quirk, requires six fields, validates both identifiers, and records one warning in the import history. It does not edit the source. A correctly labeled six-field header is also supported; incompatible layouts fail clearly.

## Import results

| Validation | Count |
| --- | ---: |
| IPNI mapping rows imported | 1,684,056 |
| Distinct raw IPNI IDs | 1,679,897 |
| Distinct WFO IDs in IPNI mappings | 1,345,147 |
| Distinct IPNI-linked WFO IDs found in current backbone | 1,344,678 |
| Distinct IPNI-linked WFO IDs absent | 469 |
| Deprecated names / distinct deprecated IDs | 43,955 |
| Deprecated IDs found in current backbone | 562 |
| Deprecated IDs absent | 43,393 |
| Deduplication mappings | 19,857 |
| Mapping rows with replacement ID found directly in current backbone | 17,266 |
| Mapping rows with replacement ID absent | 2,591 |
| Deduplicated old IDs still in current backbone | 0 |
| Self references | 0 |
| Mappings reaching cycles | 0 |
| Chains exceeding 64 replacements | 0 |
| Rejected source rows across all three imports | 0 |

There is one warning: the known missing deduplication header. Absent IDs are retained with null optional taxon links. Direct foreign-key validation counts do not imply that every missing target is resolvable through a replacement chain. An independent graph scan found 19,857 unique old IDs, no cycles, and a maximum chain depth of one in this particular source; chained mappings are exercised separately in automated tests.

Raw IPNI values remain unchanged. The separate indexed normalization field supports both bare identifiers and IPNI LSIDs. The distinct-IPNI count confirms that mappings must not be constrained to one WFO ID per IPNI ID.

| Source | SHA-256 |
| --- | --- |
| IPNI | `63c8debdf02a7bc468a03a34bb1ee99d07d7cd264c2148e2c58ad5022b618e28` |
| Deprecated names | `a72caf789a37cf158f4974571e2f62a13c3ad469aa9d63e2a7589de77f09ae1f` |
| Deduplicated IDs | `37be820742a64926803ef58e260ab3256491b7a4f133d1e543778d1e6cc35ebf` |

## Verification

- Solution build: zero warnings and errors.
- All 38 automated tests passed with PostgreSQL and full-snapshot tests enabled; none skipped. Coverage includes CSV quoting/multiline/Unicode handling, the source quirk and incompatible structures, normalized/raw IPNI values, discovery ambiguity, metadata discovery, chains, self references, cycles, traversal depth, duplicate keys, rollback, repeated/forced imports, current-backbone refreshes, authentication, ambiguity responses, and actual full-snapshot WFO/IPNI lookups.
- Running `wfo supplemental` again skipped all three successful hashes. Running `wfo all` skipped the previously imported backbone and all three supplemental files. No duplicate rows or new history entries were added.
- The backbone row count and aggregate row checksum were identical before and after supplemental imports: `count(*) = 1664014`, `sum(hashtextextended(t::text, 0)::numeric) = 11930934465045592670296`. This checks all stored taxon columns, including internal IDs, import associations, and relationships.
- Each file records its own successful history entry, source hash, release, timestamps, row counts, warnings, and validation JSON. The taxonomy statistics endpoint continues to identify the latest successful **backbone** import.
- Downloaded source files and import logs remain Git-ignored. No application-domain tables were added.

The detail endpoint now returns `{ requestedWfoId, resolvedWfoId, wasRedirected, taxon }`; consumers of the previous flat response should read taxon fields under `taxon`. See the repository README for commands and lookup semantics.
