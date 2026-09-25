# WFO 2026-09 import validation

The locally staged `data/imports/wfo/downloaded files/classification.csv` was loaded into an isolated PostgreSQL 17.9 database using the console importer. This is a TSV despite its extension. The adjacent `fileInfo.json` identifies release `2026-09`.

Source SHA-256: `87f52724e12c5be2cf954cb48910e226ae1b2ec686da6ce65ea3453e6143aefb`.

| Validation | Count |
| --- | ---: |
| Records read and imported | 1,664,014 |
| Rejected records | 0 |
| Unique TaxonIds | 1,664,014 |
| Duplicate TaxonIds | 0 |
| Missing TaxonId / ScientificName | 0 / 0 |
| Unique nonempty families | 738 |
| Unique nonempty genera | 44,957 |
| Parent references resolved / unresolved | 454,634 / 1 |
| Accepted-name references resolved / unresolved | 1,026,257 / 0 |
| Original-name references resolved / unresolved | 408,699 / 12 |
| Warnings | 27 |

The complete source has three taxonomic statuses: `Accepted` (454,635), `Synonym` (1,026,257), and `Unchecked` (183,122). These values are stored exactly; there is no enum or status constraint.

Nomenclatural statuses are `Valid` (1,144,275), `Illegitimate` (36,950), `Invalid` (30,035), `Superfluous` (4,060), `Conserved` (903), `Rejected` (692), and empty/null (447,099).

All 29 distinct ranks and all 10 major-group values (including null) are reported by the importer and stored with the import's validation JSON. The source's major groups mix codes and names; no normalization is applied.

The 27 warnings consist of 14 text fields with damaged UTF-8, one unresolved parent reference, and 12 unresolved original-name references. Damaged text receives U+FFFD with source row/field diagnostics; original bytes remain in the ignored source file. All referenced IDs remain stored, even where resolved foreign keys are null.

An independent streaming source scan found the same 1,664,014 records, all with 29 columns, and the same status distributions. The importer used about 134 MiB peak working set during this first run for the 953 MB source file. Exact memory and timing vary by machine.

The same source hash was recognized on rerun and skipped without adding an import or taxon row. A full forced reimport also succeeded with the same counts. Automated PostgreSQL tests cover stable internal IDs, failed publication rollback, empty migrated databases, future snapshots, authenticated queries, and preservation of quoted/multiline source fields. All 26 tests passed with both PostgreSQL test settings enabled, including API statistics and search/detail checks against the full imported snapshot.

Raw files, console logs, and row-level diagnostic JSONL files remain Git-ignored under `data/imports/wfo/`.
