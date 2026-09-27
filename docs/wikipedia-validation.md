# Wikipedia lead descriptions validation

The first live Wikipedia import ran on 2026-09-26 against the isolated PostgreSQL 17.9 validation database. That database already held the WFO 2026-09 backbone, the full Wikidata crosswalk and details (89,688 current items with an English Wikipedia sitelink), and the USDA PLANTS import. The `AddWikipediaReference` migration applied in about 4 seconds. Every request went to `en.wikipedia.org` with the project User-Agent, one at a time, at least 200 ms apart, with `maxlag=5`.

## Runs

A 1,000-article `wikipedia leads --limit 1000` check ran first (50 s, including the selection of all 89,688 titles). The full incremental `wikipedia leads` then checked every article and fetched the remaining leads in **59 min 5 s**, in 180 committed batches of 500 (about 25 articles per second). It completed with no warnings, no missing pages, and no errors.

| Run | Duration | Articles checked | Leads fetched | Unchanged | Rows updated |
| --- | ---: | ---: | ---: | ---: | ---: |
| `leads --limit 1000` | 0 h 00 min 50 s | 1,000 | 999 | 1 | 1,000 |
| `leads` (first full) | 0 h 59 min 05 s | 89,688 | 88,661 | 1,027 | 88,688 |
| `leads` (verification) | 0 h 10 min 01 s | 89,688 | 3 | 89,685 | 3 |

The first full run's 1,027 unchanged articles are the 1,000 checked by the limited run (whose leads were already current) plus the other 27 disambiguation pages, which are never fetched. The limited run's one unchanged article is the disambiguation page *Ipecacuanha*.

| Selection | Count |
| --- | ---: |
| Titles selected (distinct `EnwikiTitle` on current items) | 89,688 |
| Articles inserted / reactivated / retired | 89,688 / 0 / 0 |
| Item–article links | 89,688 |

Every sitelink title is distinct, so each item links to exactly one article and each article to one item. The longest title has 47 characters, so no request needed the long-query POST fallback.

**Continuation.** No batch of 20 needed `continue`: TextExtracts returned every intro in the first response. A probe before the run found the same even for 20 long crop articles (*Rice*, *Maize*, *Tea*, … with leads up to 3,280 characters). `continue` appeared only when a request asked for more intros than `exlimit` (21 titles returned 20 extracts plus `excontinue=20`). The importer follows it regardless, as the spec requires. `Fixtures/wikipedia/leads-continue-*.json` hold a real continuation captured with `exlimit=2`.

## Status

| Status | Articles | Share |
| --- | ---: | ---: |
| `Ok` | 85,443 | **95.27%** |
| `ItemMismatch` | 4,206 | 4.69% |
| `Disambiguation` | 28 | 0.03% |
| `EmptyLead` | 11 | 0.01% |
| `Missing` | 0 | 0% |
| `Pending` | 0 | 0% |

The acceptance threshold is 95% `Ok`; the rest are explained below.

**Redirects.** 4,169 sitelinks are redirects. 4,168 of them (99.1%) land on an article about a different Wikidata item and are `ItemMismatch`. The remaining one, *Atitara (plant)*, redirects to the disambiguation page *Atitara* and is `Disambiguation`, which takes precedence. **No redirect landed on an article about the requesting item**, so the case the spec calls "fine (`Ok`)" did not occur in this snapshot.

**`ItemMismatch` (4,206).** 4,168 are redirects and 38 are not. By the rank of the WFO taxon linked to the requesting item and to the page's item, the largest groups are:

| Requesting item's taxon | Page's item taxon | Articles |
| --- | --- | ---: |
| species | species | 1,921 |
| species | genus | 942 |
| genus | genus | 610 |
| species | (item has no WFO link) | 233 |
| genus | species | 123 |
| family | genus | 86 |
| (no WFO link) | (item has no WFO link) | 40 |
| genus | (item has no WFO link) | 35 |
| subspecies | species | 31 |
| order | family | 31 |

- **Synonyms redirected to the accepted name.** For 2,068 mismatches (49%), both items resolve to the same accepted WFO taxon: Wikipedia merged the synonym into the accepted name's article (for example *Kuhnistera sabinalis*, a WFO synonym, → *Dalea sabinalis*). They make up 1,503 of the 1,921 species → species mismatches.
- **Species redirected to their genus** (942), as the spec anticipated (for example *Nesohedyotis arborea* → *Nesohedyotis*).
- **Monotypic genera.** The 38 mismatches without a redirect are mostly monotypic genera whose article Wikipedia attaches to the species item, for example *Amborella* (genus item Q310470, page item Q13418082), *Austrotaxus*, *Abeliophyllum*, and *Amherstia*. *Amphiodon* is the same pattern (Q17479656 vs Q17479657).
- In every case the page belongs to another item. **3,830 of the 4,206 pages (91%) are also stored as `Ok` articles under their own item**, so excluding mismatches loses very little text. The catalog can still reach the rest through the page's own item.

**`Disambiguation` (28):** *Alpinia nutans*, *Alstroemeria pulchella*, *Atitara (plant)*, *Buchanania cochinchinensis*, *Callicarpa cana*, *Cereus haitiensis*, *Ferula erubescens*, *Guatteria glauca*, *Helichrysum rupestre*, *Helxine*, *Hibiscus hastatus*, *Hippia*, *Hopea wightiana*, *Hymenocallis caroliniana*, *Ipecacuanha*, *Ivesia*, *Melodorum fruticosum*, *Mitostigma*, *Oxalis micrantha*, *Podalyria argentea*, *Potentilla glandulosa*, *Rosa sosnovskyana*, *Salix fragilis*, *Sisymbrium sylvestre*, *Sorbus fruticosa*, *Terminalia intermedia*, *Tetilla*, and *Zephyranthes grandiflora*. They are a mix of binomials and genus names that Wikipedia uses for more than one topic. No lead is stored for them.

**`EmptyLead` (11):** TextExtracts returned an empty string (`""`, stored as such) for *Acianthera bicornuta*, *Aglaia brassii*, *Antennaria friesiana*, *Aristolochia gigantea*, *Asplenium flabellifolium*, *Deparia petersenii*, *Eleocharis macrostachya*, *Melientha*, *Notopleura uliginosa*, *Peucedanum officinale*, and *Pleopeltis christensenii*. Each still has a short description (for example "Species of orchid").

## Leads

Lengths of `Ok` leads (`LeadChars`, characters):

| Min | P10 | Median | P90 | Max |
| ---: | ---: | ---: | ---: | ---: |
| 29 | 102 | 263 | 700 | 35,693 |

These are the first full run's figures. After the verification run the minimum is 4, from the vandalized *Ephedra* lead described under [Repeatability](#repeatability), and there are 30,929 stub-like leads. The other figures are unchanged. Apart from that, the shortest are one-line stubs such as *Phyllodon (plant)* ("Phyllodon is a genus of moss.", 29), *Bryales* (30), and *Bryaceae* (31). The longest is *Frullania* (35,693), whose lead includes a list of every species in the genus; next are *Aegiphila* (8,515) and *Premna* (7,505).

**Stub-like leads:** 30,928 of 85,443 `Ok` leads (36.2%).

| Stub rule | `Ok` leads |
| --- | ---: |
| Under 200 characters | 30,584 (35.8%) |
| One sentence matching `^[^.]+ is a (species\|genus) of` | 7,563 (8.9%) |
| Either (stub-like) | 30,928 (36.2%) |

A "single sentence" means no `.`, `!`, or `?` followed by whitespace and more text. Typical examples: "Bulbophyllum luanii is a species of orchid in the genus Bulbophyllum." and "Microseris heterocarpa, known by the common name grassland silverpuffs, is a species of flowering plant in the family Asteraceae." These are accurate but say little beyond the taxonomy. The catalog should rank them below richer sources.

Texts are stored exactly as TextExtracts returns them, including its artifacts. For example, removed pronunciations leave "Hosta (, syn. Funkia) …".

**Short descriptions:** 85,423 of 85,443 `Ok` articles (99.98%) have one. The most common are "Species of flowering plant" (27,376), "Species of plant" (11,562), "Genus of flowering plants" (6,483), "Species of orchid" (4,821), "Species of tree" (3,642), and "Species of legume" (3,115).

## By rank

Rank comes from the WFO taxon linked (`WfoTaxonId`) to each article's Wikidata item:

| Rank | Articles | `Ok` | Share |
| --- | ---: | ---: | ---: |
| species | 73,047 | 69,879 | 95.66% |
| genus | 14,947 | 14,171 | 94.81% |
| infraspecific (subspecies, variety, form, …) | 631 | 560 | 88.75% |
| other (family, order, section, …) | 812 | 624 | 76.85% |
| item without a current WFO link | 261 | 219 | 83.91% |

An article counts once per row its item's links reach: 4 articles link to taxa of two ranks, and a few items have both a resolved and an unresolved WFO link, so the rows sum to 89,698. Almost every non-`Ok` article in each row is an `ItemMismatch`; infraspecific and higher-rank sitelinks more often redirect to a broader article, which is why their shares are lower.

## Garden check

Each taxon was resolved through WFO → Wikidata link → sitelink, not by title. For ACSA3, the path starts from its USDA link's accepted taxon.

| Taxon | Item | Status | Lead (first 150 characters) |
| --- | --- | --- | --- |
| ACSA3 (*Acer saccharum*) | Q214733 | `Ok` | Acer saccharum, the sugar maple, is a species of flowering plant in the soapberry and lychee family Sapindaceae. It is native to the hardwood forests |
| *Acer palmatum* | Q269224 | `Ok` | Acer palmatum, commonly known as Japanese maple, palmate maple, or smooth Japanese maple (Korean: danpungnamu [단풍나무]; Japanese: irohamomiji [イロハモミジ] o |
| *Rudbeckia hirta* | Q2532820 | `Ok` | Rudbeckia hirta, commonly called black-eyed Susan and yellow coneflower, is a North American flowering plant in the family Asteraceae. It grows to 1 m |
| *Echinacea purpurea* | Q272661 | `Ok` | Echinacea purpurea, the eastern purple coneflower, purple coneflower, hedgehog coneflower, or Echinacea, is a North American species of flowering plan |
| *Hosta* (genus) | Q623347 | `Ok` | Hosta (, syn. Funkia) is a genus of plants commonly known as hostas, plantain lilies and occasionally by the Japanese name gibōshi. Hostas are widely |
| *Cornus florida* | Q887221 | `Ok` | Cornus florida, the flowering dogwood or American dogwood, is a species of flowering tree in the family Cornaceae native to eastern North America and |

Each resolved to exactly one item and article, with no synonym items carrying their own sitelink.

## Repeatability

A second incremental `wikipedia leads`, started right after the first finished, checked all 89,688 revisions in 10 min 1 s. It fetched and rewrote exactly 3 leads whose `lastrevid` had changed since the first fetch: *Ephedra (plant)*, *Madhuca longifolia*, and *Pinus armandii*. Every other row, including all selection, link, and metadata rows, was left untouched (0 inserted, retired, or otherwise updated). The status distribution, redirect counts, by-rank counts, and garden check matched the first run exactly, and there were no warnings.

A routine incremental run therefore takes about 10 minutes, because checking needs one request per 50 articles. A first run takes about an hour, because fetching leads needs one request per 20.

**Vandalism is staged as-is.** The *Ephedra (plant)* change was vandalism: at 23:41–23:42 UTC a temporary account replaced the lead with a single four-letter word, and the verification run fetched it (revision 1376913903, `LeadChars` 4) before anyone reverted it. This is the specified behavior. The text is stored unmodified with the revision it came from, and the next run replaces it once the revert lands, because a revert is a new revision. But it means **any staged lead can be a vandalized revision**. The catalog must not publish leads without a guard, such as holding back very short leads or ones that shrank sharply between revisions, or preferring revisions that have been stable for some time.

## Storage

`wikipedia_article` uses 73 MB including its indexes and TOAST (lead text), and `wikipedia_item_article` uses 10 MB.

## Test runs

All 282 tests passed with both `ACBF_TEST_POSTGRES` and `ACBF_TEST_WFO_SNAPSHOT` set against the validation server, including the full-snapshot WFO, Wikidata, and USDA smoke tests. `dotnet build` reported no warnings. The parser tests run against real responses captured from `en.wikipedia.org` on 2026-09-26 (`tests/AnythingCanBeFarming.Api.Tests/Fixtures/wikipedia/`), and the URL tests compare against Wikipedia's own `canonicalurl` values. No test contacts Wikipedia.
