# Anything Can Be Farming

ACBF uses Angular, an ASP.NET Core controller API, PostgreSQL, and authentication against an existing Keycloak realm. Aspire orchestrates local development only. The first reference dataset is the World Flora Online (WFO) Taxonomic Backbone. Plant, yard, and garden domain features are not implemented.

## Prerequisites

- .NET 10 SDK, version 10.0.300 or later in the 10.0 SDK family (`global.json` controls selection).
- Docker Desktop with **Linux containers** enabled and the engine running. On Linux, use a running Docker Engine. Allow Docker to access the repository for bind mounts.
- An IDE with .NET 10 and Aspire debugging support, such as Visual Studio 2026 with the ASP.NET/web workload and Aspire tooling. Other IDEs need their corresponding Aspire integration to attach the debugger to project resources automatically.
- Network access to NuGet, npm, Docker Hub, and the configured Keycloak server for initial setup.

No host Node.js, npm, Angular CLI, global Aspire CLI, or Aspire workload is needed for the normal F5 workflow. The AppHost SDK supplies Aspire tooling; the frontend image supplies Node 22.22.3 and npm 11.20.0. Angular dependencies are installed from the committed lockfile into a Docker volume on first startup.

Trust the ASP.NET development certificate once:

```sh
dotnet dev-certs https --trust
```

For Linux, also follow the SDK's browser trust instructions if your browser uses a separate certificate store. Do not disable HTTPS validation to fix a trust error.

## Normal workflow

1. Clone this repository and start Docker Desktop/the Docker engine.
2. Verify the Keycloak client settings below. Apply any local configuration overrides needed for your environment.
3. Open `AnythingCanBeFarming.sln`.
4. Set **AnythingCanBeFarming.AppHost** as the startup project, using its `https` launch profile.
5. Press **F5** and open the frontend from the Aspire dashboard or at `http://localhost:4200`.

After F5:

| Resource | Execution | Purpose |
| --- | --- | --- |
| `acbf-postgres` | PostgreSQL 17.9 container | Persistent `acbf` database |
| `acbf-web` | Angular 22 development container | Source watching and browser hot reload |
| `acbf-api` | `AnythingCanBeFarming.Api` .NET project on the host | HTTPS API under the IDE debugger |
| Existing Keycloak realm | Remote server | Sign-in and token issuance |

The first launch downloads packages and container images. The API waits for PostgreSQL and database creation before starting. Angular starts independently so its diagnostics remain available if the API is unavailable. There is no `docker compose up` step and no API Dockerfile.

Set a breakpoint in `StatusController.Get` and click **Check connections** to hit it. `AuthController.Me` is another breakpoint target after signing in. Starting the AppHost with `dotnet run` launches the same resources, but a CLI launch does not itself attach an IDE debugger:

```sh
dotnet run --project src/AnythingCanBeFarming.AppHost
```

| Endpoint | Default URL |
| --- | --- |
| Frontend | http://localhost:4200 |
| API | https://localhost:7243 |
| API HTTP listener, redirects to HTTPS | http://localhost:5243 |
| Swagger UI, development only | https://localhost:7243/swagger |
| OpenAPI document, development only | https://localhost:7243/swagger/v1/swagger.json |
| Aspire dashboard | https://localhost:17243 |

The dashboard login link is emitted by the AppHost. PostgreSQL gets a dynamic loopback host port; its endpoint and injected connection information are available in the dashboard. The frontend calls the host API directly from the browser using HTTPS and development CORS.

## Configuration

Ordinary ASP.NET configuration applies: committed `appsettings.json` defaults, development settings, user secrets, environment variables, and command-line overrides. No passwords or client secrets belong in the repository. `.env` files are ignored but are **not automatically loaded** by these projects.

Use the AppHost's settings for the usual orchestrated workflow:

| AppHost setting | Default / behavior |
| --- | --- |
| `Authentication:Authority` | `https://auth.jeandervin.com/realms/hooviepack`; forwarded to API and frontend |
| `Authentication:Audience` | `acbf-api`; forwarded to API |
| `Web:ClientId` | `acbf-web`; forwarded to frontend |
| `Web:Origin` | `http://localhost:4200`; web port and API's allowed development origin |
| `Web:ApiBaseUrl` | `https://localhost:7243`; browser-facing API address |
| `Parameters:acbf-postgres-password` | Aspire generates and persists a random local user secret if absent |

For example, override the realm without editing committed files:

```sh
dotnet user-secrets set "Authentication:Authority" "https://your-keycloak.example/realms/your-realm" --project src/AnythingCanBeFarming.AppHost
```

The API independently supports:

| API setting | Purpose |
| --- | --- |
| `ConnectionStrings:acbf` | Injected by AppHost's database reference; required when running the API separately |
| `Authentication:Authority` | Exact Keycloak realm issuer; HTTPS metadata is required |
| `Authentication:Audience` | Expected access-token audience |
| `Cors:AllowedOrigins` | Array of allowed development browser origins |
| `PlantNet:ApiKey` | Private Pl@ntNet API key used by the server-side `PlantNetClient` |

For an intentional standalone API launch, set `ConnectionStrings:acbf` with **Manage User Secrets** on the API project. Example shape, using your own credentials and database port:

```json
{
  "ConnectionStrings": {
    "acbf": "Host=localhost;Port=<port>;Database=acbf;Username=postgres;Password=<local-password>"
  }
}
```

Environment-variable equivalents include `ConnectionStrings__acbf`, `Authentication__Authority`, `Authentication__Audience`, and `Cors__AllowedOrigins__0`. During orchestration, AppHost's injected environment values take precedence over API user secrets.

Frontend public runtime settings are generated into the ignored `apps/web/public/config.json` at container startup. `apps/web/config.example.json` documents the shape and provides defaults. AppHost supplies `ACBF_KEYCLOAK_ISSUER`, `ACBF_KEYCLOAK_CLIENT_ID`, and `ACBF_API_BASE_URL`. The app fetches this configuration before bootstrapping; nothing in it is secret. Restart the web resource after configuration changes.

If you change the API's HTTPS port in its `Properties/launchSettings.json`, update `Web:ApiBaseUrl` as well. If you change the frontend origin, update Keycloak's redirects, post-logout redirects, and web origin to match. `Web:Origin` assumes local HTTP development; the container serves HTTP on internal port 4200.

## Pl@ntNet API client

The API registers `AnythingCanBeFarming.Api.PlantNet.PlantNetClient` as a typed HTTP client for the supplied My Pl@ntNet v2.2.2 specification. Inject it into server-side services to access plant, disease, and variety identification, embeddings, taxonomy lists, health, quotas, and subscription details. It sends the configured key as the `api-key` query parameter. HTTP client request logging is disabled for this client because the query contains the key.

`PlantNet:ApiKey` is blank in the API's `appsettings.json`. Set it using API project user secrets (also used when the API is launched through Aspire):

```sh
dotnet user-secrets set "PlantNet:ApiKey" "<your-api-key>" --project src/AnythingCanBeFarming.Api
```

Alternatively, set `PlantNet__ApiKey` in the API process environment. The app can start without a key; authenticated Pl@ntNet calls fail with a configuration error until it is set. The public health call needs no key.

For example, with an injected `PlantNetClient plantNet`:

```csharp
var image = new PlantNetImageUpload(
    await File.ReadAllBytesAsync("leaf.jpg", cancellationToken), "leaf.jpg", "image/jpeg");
var result = await plantNet.IdentifyAsync([image], options: new()
{
    Organs = ["leaf"],
    Language = "en",
    NumberOfResults = 5
}, cancellationToken: cancellationToken);
```

Identification accepts one to five JPEG/PNG images of the same plant. Omit organs for automatic detection, or supply one per image. POST requests are subject to the API's 50 MiB limit. The deprecated URL-based GET operation is available as `IdentifyUrlsAsync`. Species pagination accepts strings so setting both `Page` and `PageSize` to `""` disables pagination as specified by the API. Non-success responses throw `HttpRequestException` with the HTTP status; calls accept cancellation tokens and do not retry quota-consuming requests automatically. The registered client times out after 30 seconds.

## Plant identifier

The frontend's **Identify** page (`/identify`, the default route) takes 1–5 photos of one plant, sends them to Pl@ntNet through the API, and lists every match with its confidence, names, and credited reference photos. Nothing is saved. It needs a signed-in user and `PlantNet:ApiKey` (see above).

Photos are prepared in the browser before upload: EXIF rotation is applied, the long edge is limited to 1600 px, and the image is re-encoded as JPEG. This strips EXIF metadata such as GPS coordinates and converts PNG and HEIC to JPEG. If the browser can't decode a photo (for example, HEIC in a browser without HEIC support), the page says the format isn't supported instead of uploading it.

`POST /api/identify` requires a bearer token and takes `multipart/form-data`:

| Field | Value |
| --- | --- |
| `images` | 1–5 files, each JPEG or PNG (checked by content, not name or type) and at most 8 MiB |
| `organs` | One per image, in the same order: `auto`, `leaf`, `flower`, `fruit`, `bark`, `habit`, `branch`, `bud`, `seed`, `other`, `scan`, `sheet`, `drawing`, `anatomy`, or `aerial` |

A successful response contains `bestMatch`, `remainingRequests`, `predictedOrgans` (what Pl@ntNet saw in each `auto` photo, by image index), and `results` (all matches in score order, with names, taxonomy, GBIF/POWO ids, and HTTPS reference images with credits). When Pl@ntNet finds no plant, the response is still **200**, with empty `results`. Errors return `{ "error": "<code>", "message": "<text>" }`:

| Status | `error` |
| --- | --- |
| 400 | `no_images`, `too_many_images`, `organ_count_mismatch`, `invalid_organ`, `image_too_large`, `unsupported_image`, `empty_image` |
| 429 | `quota_exceeded` |
| 502 | `upstream_rejected` (Pl@ntNet refused the photos) or `upstream_error` (failure, timeout, or bad response) |
| 503 | `identification_unavailable` (`PlantNet:ApiKey` not set) |

To try it, start the AppHost with the key set, open http://localhost:4200, sign in, and use **Add photo** (camera on phones) or **Choose from library**. To call the API directly, copy an access token from browser Network tools (do not commit or share it):

```sh
curl -H "Authorization: Bearer <token>" -F images=@leaf.jpg -F organs=leaf \
  https://localhost:7243/api/identify
```

Each identification uses one request from the Pl@ntNet daily quota.

## Keycloak setup — manual only

Use the existing realm at `https://auth.jeandervin.com/realms/hooviepack`, or override the authority. Realm reuse is intentional; **do not reuse or modify existing HooviePack clients**. No script in this repository modifies Keycloak.

Create or verify these two OpenID Connect clients:

| Setting | `acbf-web` | `acbf-api` |
| --- | --- | --- |
| Client authentication | Off (public SPA) | On; no secret is used by this API |
| Standard flow | On | Off |
| Implicit flow | Off | Off |
| Direct access grants | Off | Off |
| Service accounts | Off | Off |
| Authorization services | Off | Off |
| PKCE method | `S256` | Not applicable |
| Valid redirect URIs | `http://localhost:4200/*` | None |
| Valid post-logout redirect URIs | `http://localhost:4200/*` | None |
| Web origins | `http://localhost:4200` | None |

The API client represents the resource audience; it does not log users in. Enable the `profile` client scope for `acbf-web` so username/name claims are available.

In **acbf-web → Client scopes → acbf-web-dedicated → Mappers**, add an **Audience** mapper:

- Name: `acbf-api-audience`
- Included Client Audience: `acbf-api`
- Add to access token: On
- Add to ID token: Off

Using the dedicated scope makes this audience apply without roles or additional requested scopes. Evaluate a token and verify `aud` includes `acbf-api`. If your Keycloak version enables lightweight access tokens, ensure the audience mapper also includes the claim in those tokens. See the [Keycloak audience documentation](https://www.keycloak.org/docs/latest/server_admin/#_audience).

The frontend uses Authorization Code Flow with S256 PKCE and no client secret. Access and refresh tokens stay in memory. Reloads restore authentication through Keycloak's SSO cookie: a silent check where supported, or a normal redirect where third-party cookies are blocked. The adapter refreshes the access token before protected API calls. Logout redirects through Keycloak and back to the frontend. See the [Keycloak JavaScript adapter](https://www.keycloak.org/securing-apps/javascript-adapter).

The API validates JWT signatures, issuer, audience, and lifetime using Keycloak's discovery/JWKS metadata. It returns 401 for missing or invalid tokens. No application user table, role policy, or token-issuing API is implemented.

## Frontend development

Edit files under `apps/web/src` on the host. Aspire bind-mounts `apps/web` at `/app`; Angular polls for changes every second, including on Windows/WSL bind mounts. Hot reload/live reload remains enabled.

Two nested named volumes keep generated container files separate:

- `acbf-web-node-modules` → `/app/node_modules`
- `acbf-web-angular-cache` → `/app/.angular`

The startup script fingerprints `package.json`, `package-lock.json`, and the Node runtime/platform. Unchanged dependencies skip installation. After dependency changes, restart the web resource in Aspire to rerun `npm ci`. After Dockerfile changes, restart the AppHost so Aspire rebuilds the image.

To add a dependency without installing Node on the host, find the web container name with `docker ps --filter name=acbf-web`, then run:

```sh
docker exec <web-container> npm install <package-name>
```

Commit both package files. To run checks in the active development container:

```sh
docker exec <web-container> npm run build
docker exec <web-container> npm test -- --watch=false
```

Optional host tooling for checks is Node 22.22.3 or a compatible newer runtime, plus npm 11.20.0. Run `npm ci`, `npm run build`, and `npm test -- --watch=false` from `apps/web`. A global Angular CLI is unnecessary. The standard development server remains containerized.

## Verification

```sh
dotnet build AnythingCanBeFarming.sln
dotnet test tests/AnythingCanBeFarming.Api.Tests
```

API tests exercise the actual JWT bearer handler with locally signed test tokens, including rejection of bad issuer, audience, signature, expiry, and malformed tokens. They also check CORS, anonymous/protected endpoints, health failure responses, and the reference-only EF model. WFO, Wikidata, and Wikipedia tests cover parsing (Wikipedia against saved live responses), stubbed Wikidata and Wikipedia HTTP (no test contacts either), and optionally real PostgreSQL imports, resolution, and search (see below). The remote Keycloak installation is not used or modified by tests. Identify endpoint tests cover validation, magic-byte sniffing, the request sent to a stubbed Pl@ntNet transport, response mapping, and error mapping; a drift test keeps the organ enum in step with the client. Frontend tests cover session restoration settings, token renewal, sign-in/out, restricting token attachment to the API, routing, the status page, image preparation, and the Identify page.

With AppHost running:

```sh
curl https://localhost:7243/api/status
curl https://localhost:7243/api/status/database
curl -i https://localhost:7243/api/auth/me
```

Expected: `{"status":"ok"}`, `{"database":"connected"}`, and **401** respectively. Use `curl.exe` in Windows PowerShell if `curl` is aliased. The database endpoint uses ASP.NET Core health checks and returns **503** with `{"database":"unavailable"}` if a connection cannot be opened; it never returns connection details or exception text.

Complete the interactive checks:

1. Open the frontend and select **Status**. API and Database should show **Connected**.
2. Click **Sign in**, authenticate with your existing Keycloak account, and confirm **Signed in as …** and **Protected API: Verified as …**.
3. In browser Network tools, confirm `/api/auth/me` has an `Authorization: Bearer …` header and returns 200. Do not paste tokens into tickets or committed files.
4. Reload; the Keycloak SSO session should restore the signed-in state. Click **Sign out** and verify the signed-out state.
5. Edit the subtitle in `apps/web/src/app/status/status-page.html`; confirm an Angular rebuild and browser update without restarting the AppHost.
6. Hit the C# breakpoint described above while debugging the AppHost.

`AcbfDbContext` is shared by the API and importer through `AnythingCanBeFarming.Data`. It contains only reference entities (WFO, Wikidata, USDA PLANTS, Wikipedia, and the source-neutral `source_import` history). Startup and health checks do not call `EnsureCreated`, `Migrate`, or import reference data. Apply migrations explicitly before using reference endpoints; an empty migrated database returns empty search results, zero statistics, and 404 for unknown taxa.

## WFO reference data

Stage the original WFO files anywhere under `data/imports/wfo/`. All contents except `.gitkeep` are Git-ignored, including release metadata and generated diagnostics. The importer recursively discovers tab-delimited files with a WFO header, including the supplied `downloaded files/backbone/classification.csv`. If there is more than one snapshot, select one using `--file`. It never modifies the source.

The importer uses the API's `ConnectionStrings:acbf`: API appsettings, environment-specific appsettings, the API's development user secrets, then environment variables. Its environment defaults to Development; set `DOTNET_ENVIRONMENT` (or `ASPNETCORE_ENVIRONMENT`) to change that. It does not need Keycloak configuration or tokens. Aspire injects the database connection into the API process only; a separately launched importer needs the loopback connection shown in the Aspire dashboard configured through API user secrets or `ConnectionStrings__acbf`.

With that connection configured, run from the repository:

```sh
dotnet tool restore
dotnet ef database update --project src/AnythingCanBeFarming.Data --startup-project src/AnythingCanBeFarming.Api
dotnet run --project src/AnythingCanBeFarming.DataImport -- wfo
```

Migration and import are separate operations. The migration creates `reference.wfo_import`, `reference.wfo_taxon`, indexes, and foreign keys. No data is imported during API startup or migration.

Optional import arguments:

```sh
dotnet run --project src/AnythingCanBeFarming.DataImport -- wfo --file "data/imports/wfo/downloaded files/backbone/classification.csv"
dotnet run --project src/AnythingCanBeFarming.DataImport -- wfo --version 2026-06 --force
```

Version precedence is an explicit `--version` override, adjacent `fileInfo.json`'s `version`, then a YYYY-MM or YYYY_MM release in the filename. Unknown versions remain null. A SHA-256 match to any successful import is skipped unless `--force` is supplied; forcing an older snapshot deliberately makes that snapshot current again.

CsvHelper parses the exact 29-column TSV format, including quoted tabs, escaped quotes, multiline fields, and Unicode. Records stream through Npgsql binary COPY into a temporary staging table; memory does not grow with dataset size. Fields allow up to 16 MiB to prevent an unterminated quote from consuming the entire file. Optional empty strings become null; identifiers, names, authorship, statuses, and other nonempty strings are not trimmed, normalized, or reconstructed. The supplied date-only values use PostgreSQL `date`.

Missing required IDs/names, malformed records, NULs, and duplicate TaxonIds fail publication and return a nonzero exit code. All conflicting duplicate rows are reported. No rejected snapshot marks existing taxa non-current. Invalid dates become null with a row/field warning. Invalid UTF-8 in text fields is decoded with U+FFFD and reported; the unchanged source retains the original bytes. A replacement character in an identifier rejects its record. Warning messages also flag U+FFFD already present in the source.

Imports serialize with a PostgreSQL advisory lock. Publication, relationship updates, and successful metadata commit in one transaction. Existing TaxonIds retain their internal bigint IDs, new taxa are inserted, and absent taxa become non-current without deletion. Raw relationship identifiers remain intact. Resolved foreign keys refer to the current snapshot only; missing targets and non-current records have null resolved links. Parent, accepted-name, and original-name links use explicit WFO identifiers, without name-string inference or assumptions about statuses.

Failed/cancelled imports roll back snapshot changes and record failed metadata. If the process is killed or loses its database connection before failure metadata can be saved, the next import identifies the abandoned Running entry after acquiring the exclusive import lock. This is a current reference snapshot model, not complete historical row versioning; `ImportId` on an absent taxon identifies the last snapshot containing it.

The console prints counts for records read/imported/rejected, duplicate and missing IDs/names, unique families/genera, all four status/rank/group distributions, and resolved/unresolved relationships. The same summary is stored in `wfo_import.ValidationJson`. Detailed diagnostics stream to an adjacent `wfo-import-*.jsonl` file; `--diagnostics <new-path>` chooses another location. Rows are physical source line numbers, with the header at line 1. Remarks are never dumped into diagnostics. Warnings count individual field/reference issues, not necessarily distinct taxa.

Reference endpoints use the existing JWT bearer authentication:

| Endpoint | Behavior |
| --- | --- |
| `GET /api/reference/plants/search?q=acer&pageSize=20` | Ranked search by scientific name, genus, and English common name; one row per accepted taxon (see [Wikidata enrichment](#wikidata-enrichment)) |
| `GET /api/reference/plants/{taxonId}` | Resolves explicit replacement chains and returns `{ requestedWfoId, resolvedWfoId, wasRedirected, taxon }`; 404 if no current target exists |
| `GET /api/reference/plants/by-ipni/{ipniId}` | Normalizes a bare or IPNI LSID identifier, resolves its WFO mappings, and returns the same resolution envelope; 409 for multiple current targets |
| `GET /api/reference/plants/stats` | Total retained and current counts, current families/genera and distributions, relationship counts, and latest successful backbone import |

Search defaults to 20 results. `ReferencePlants:DefaultPageSize` and `ReferencePlants:MaxPageSize` configure limits, with a hard ceiling of 100. Invalid queries or page sizes return 400. Search omits large remarks and publication fields. Detail fields such as `taxonRemarks` remain untrusted source text; clients must not render them as trusted HTML. Statistics use one consistent database snapshot and report current-record distributions, including null values.

To enable the PostgreSQL integration test, set `ACBF_TEST_POSTGRES` to a test server connection whose user can create databases, then run `dotnet test tests/AnythingCanBeFarming.Api.Tests`. The test creates and removes its own uniquely named database, exercising migrations, bulk loading, repeat/forced imports, failure rollback, future snapshots, and authenticated endpoints. Without that setting it is reported as skipped. To additionally run a read-only API smoke test against an already imported WFO snapshot, set `ACBF_TEST_WFO_SNAPSHOT` to that database connection. Neither test contacts Keycloak.

See [the first full-snapshot validation](docs/wfo-validation.md) for observed counts and source-quality findings.

## WFO supplemental identifiers

Apply the migrations above, then import just the supplemental CSV files without reimporting the backbone:

```sh
dotnet run --project src/AnythingCanBeFarming.DataImport -- wfo supplemental
```

Use `wfo all` to run backbone, IPNI, deprecated names, and deduplicated IDs in order. `wfo` and `wfo backbone` retain the backbone-only command. `--directory "data/imports/wfo/downloaded files"` selects a package; discovery uses stable `_ipni_to_wfo.csv`, `_deprecated_names_lookup.csv`, and `_deduplicated_ids_lookup.csv` suffixes (or the unprefixed filenames), ignoring numeric prefixes. Missing or ambiguous files fail before imports start. `--file` selects only the backbone; `--force` and `--version` apply to all selected imports.

Each supplemental file has its own `reference.wfo_import` entry with dataset kind, filename, SHA-256, release, timestamps, row counts, warnings, status, and validation JSON. Existing history defaults to `Backbone`, so taxonomy statistics continue to report the backbone release. Release metadata comes from `--version`, adjacent metadata/filename, or the same package's `backbone/fileInfo.json`; unknown releases remain null. These files' local metadata says `2026-09`; the importer does not infer a different release from modification timestamps.

**Known source quirk:** the staged `050_deduplicated_ids_lookup.csv` has the five-field header `wfo_id,name_canonical,authors_string,rank,nomenclatural_status` but every inspected data row has six fields. The missing second header is interpreted as `replacement_wfo_id`. Data order is **deprecated WFO ID, replacement WFO ID, canonical name, authors, rank, nomenclatural status**. For example, `wfo-4000048768,wfo-4000048766,?,,genus,deprecated` maps the first ID to the second. The importer recognizes this exact header/layout, emits a persisted warning, and requires six fields and valid WFO identifiers in positions 1 and 2. It also accepts an explicit six-column header with `replacement_wfo_id` second. Other headers, shifted columns, malformed CSV, invalid UTF-8, empty required identifiers, NULs, and duplicate natural keys reject publication. The downloaded file is never rewritten.

CsvHelper streams quoted, comma-containing, multiline, and Unicode fields through binary COPY staging. Each file publishes atomically. Supplemental records are upserted by their natural keys; prior historical mappings absent from a later file are retained. Conflicting mappings for an existing deprecated ID are updated by the new import. Failed files leave previously committed data intact; earlier successful files in a multi-file run stay committed and are skipped on retry. Identical successful hashes are skipped unless forced. These imports never update `wfo_taxon` or create application-domain tables.

`IpniId` preserves the source exactly; indexed `NormalizedIpniId` strips the `urn:lsid:ipni.org:names:` prefix and surrounding whitespace for matching. Multiple WFO mappings per IPNI ID remain valid. Optional taxon foreign keys resolve raw IDs directly against the current backbone, leaving absent IDs null. Backbone refreshes also refresh these supplemental links. Validation counts describe the complete stored supplemental tables at import time; IPNI WFO counts are distinct IDs, while deduplication replacement counts are mapping rows with directly resolved targets.

Canonical resolution follows prescribed replacement edges before returning a current backbone record, including when the old ID remains in that backbone. It allows up to 64 replacements, detects visited IDs/self references, and never follows taxonomy synonyms implicitly. Invalid graphs reject the deduplication import; the API also returns 409 if an invalid graph is introduced outside the importer. The report counts mappings reaching cycles (including upstream mappings), rather than claiming a count of distinct cycle components. A deprecated-name record alone does not imply a replacement. Missing/non-current terminal targets return 404. Multiple IPNI mappings converging on one current taxon return that taxon; distinct current targets return 409 with candidate WFO IDs.

The detail endpoint now returns the resolution envelope shown above instead of the previous flat response. The nested taxon retains source fields and one-level related taxa, but omits import IDs and internal relationship IDs. Search queries current backbone taxa and Wikidata common names; supplemental records are not added as separate search results.

See [supplemental validation](docs/wfo-supplemental-validation.md) for the staged-file counts and checks.

## Wikidata enrichment

Wikidata is the first enrichment source. It links WFO taxa to Wikidata items through the WFO ID property (P7715) and stores English and other-language common names (P1843), the English Wikipedia article title, the first image's Commons file name (P18), and other sources' IDs: GBIF (P846), IPNI (P961), USDA PLANTS (P1772), and POWO (P5037). Apply the migrations and import the WFO backbone first, then run:

```sh
dotnet run --project src/AnythingCanBeFarming.DataImport -- wikidata crosswalk [--allow-shrink]
dotnet run --project src/AnythingCanBeFarming.DataImport -- wikidata details [--full] [--limit N]
dotnet run --project src/AnythingCanBeFarming.DataImport -- wikidata all [--allow-shrink] [--full] [--limit N]   # crosswalk, then details
dotnet run --project src/AnythingCanBeFarming.DataImport -- wikidata resolve
```

It uses the same `ConnectionStrings:acbf` lookup as the WFO importer. Every run records a `reference.source_import` row (source `Wikidata`, kind `Crosswalk` or `Details`) with its CLI options, counts, validation JSON, and a sanitized error message; runs for one source serialize on an advisory lock, and a `Running` row left by a killed process becomes `Abandoned` on the next run.

- **Crosswalk** queries the Wikidata Query Service for every non-deprecated P7715 statement, in 11 partitions by the WFO ID's last character (0–9, plus IDs not ending in a digit). A partition that times out or errors is split into ten two-character partitions, each tried at most twice. It publishes in one transaction: items and links seen are upserted, links and items no longer seen become `IsCurrent = false` (nothing is deleted), and every current link is resolved against WFO. An empty result, or one with fewer than half the current links, fails publication as a likely outage; `--allow-shrink` overrides the 50% check. It takes about 5 minutes.
- **Details** calls `wbgetentities` in batches of 50. By default it first asks for `lastrevid` only and refetches items that were never fetched, changed, or were redirected; `--full` refetches everything and `--limit N` stops after N items. It commits every 500 items, so an interrupted run resumes where it stopped. **The first full run takes about 4 hours** (about 920,000 items), and a routine incremental run still takes about 2½ hours because it checks every revision; both can be stopped and rerun safely. Each fetched item's common names and external IDs are replaced with exactly the fetched set. Missing (deleted) items are reported and keep their stored details; a redirected item's details are stored under the target QID and the old item is retired. P7715 values in the entity are compared with the crosswalk and mismatches reported; the crosswalk stays authoritative for links.
- **Resolution** follows WFO deduplication replacements to a current taxon, then sets the accepted taxon: the taxon itself if `Accepted`, its current accepted taxon if a `Synonym`, otherwise null. Malformed WFO IDs get `NotFound` and a warning. Links are never inferred from names. Resolution also runs automatically at the end of every `wfo` import command, because backbone and deduplication refreshes change current taxa; `wikidata resolve` runs it on its own.

Before each phase, the importer checks that every property it reads still has the expected datatype and fails fast if one changed. Raw values are stored exactly as Wikidata states them; `NormalizedName` sits alongside the raw common name (NFKD, combining marks removed, invariant lowercase, curly quotes folded, whitespace collapsed), and PostgreSQL generates `CompactName` from it with spaces and hyphens removed. Rerunning with no upstream change writes no rows.

**Good citizenship.** Every request sends `User-Agent: AnythingCanBeFarming/<version> (https://github.com/jjeandervin/anything-can-be-farming)`; override it with `Wikidata:UserAgent`. SPARQL requests are at least 2 seconds apart and Action API requests at least 200 ms apart, one at a time, with `maxlag=5`. HTTP 429 and 503 (and `maxlag` errors) are retried up to 5 times, honoring `Retry-After` (default 10 seconds); other 4xx responses are not retried. Ctrl+C cancels cleanly. `Wikidata:SparqlBaseAddress` and `Wikidata:ApiBaseAddress` exist for testing against a stub.

**Licensing.** Wikidata data is CC0, so no attribution is legally required. Every row still records its source (`source_import`, `DetailsImportId`, `CrosswalkImportId`) so the app can credit Wikidata. All Wikidata strings are untrusted source text and must never be rendered as HTML.

**Search contract (breaking change).** `GET /api/reference/plants/search`:

- `q` must be 2–200 characters after trimming, otherwise 400. Two-character queries cannot use the trigram indexes and take a few seconds on the full snapshot.
- It matches current scientific names and genera (ILIKE, backed by trigram indexes) and English common names (`en` and `en-*`), comparing normalized names with the normalized query. Specific epithets are no longer matched on their own.
- Common names also match with spaces and hyphens ignored, so `black-eyed susan` finds Wikidata's "blackeyed Susan". This goes beyond the original spec, which kept hyphens.
- It returns one row per accepted taxon. A synonym match is reported against its accepted taxon, and a common name against its link's accepted taxon; taxa without one (for example `Unchecked`) appear as themselves.
- Ranking: exact; then an exact match with spaces and hyphens ignored; then prefix; then word prefix (start of any word, after a space or hyphen); then substring; then any other space- and hyphen-insensitive match. Within a tier, a match on the taxon's own name or common name beats a synonym match. Remaining ties go to `Accepted` status, then to higher ranks (genus before species before infraspecific), then ordinal scientific name, then `TaxonId`.
- Each result adds `commonName` (the best English name: `en` over `en-*`, then a name that matched the query, then the shortest, then ordinal order), `matchedOn` (`scientificName`, `synonym`, or `commonName`), and `matchedText`.

```jsonc
{ "taxonId": "wfo-0000514950", "scientificName": "Acer palmatum", "scientificNameAuthorship": "Thunb.",
  "taxonRank": "species", "taxonomicStatus": "Accepted", "family": "Sapindaceae", "genus": "Acer",
  "commonName": "Japanese maple", "matchedOn": "commonName", "matchedText": "Japanese maple" }
```

See [Wikidata validation](docs/wikidata-validation.md) for observed counts, coverage, and search timings.

## USDA PLANTS traits

USDA PLANTS characteristics (growth habit, duration, tolerances, heights, bloom, colors, and more) and state distribution are staged from the Encyclopedia of Life's Darwin Core Archive: **"USDA PLANTS structured data DwCA", Zenodo record 18945513, version 8 (2026-03-10), [doi:10.5281/zenodo.18945513](https://doi.org/10.5281/zenodo.18945513)**. The underlying data is USDA NRCS, The PLANTS Database (https://plants.usda.gov), a US government work; credit it as the source. Values are staged exactly as the archive states them. The curated catalog, trait precedence, and zone calculation are a later spec.

Download `usda_plant_traits.tar.gz` from the Zenodo record and extract it under `data/imports/usda/`. Everything there except `.gitkeep` is Git-ignored, including the `usda-import-*.jsonl` diagnostics. The importer finds `meta.xml` in that folder or its only subfolder and never modifies the files. Apply the migrations, import the WFO backbone (and ideally Wikidata, which supplies most links), then run:

```sh
dotnet run --project src/AnythingCanBeFarming.DataImport -- usda [--directory <archive>] [--force] [--version 8] [--zenodo-record 18945513] [--diagnostics <jsonl>]
dotnet run --project src/AnythingCanBeFarming.DataImport -- usda link
dotnet run --project src/AnythingCanBeFarming.DataImport -- usda verify [--sample 20] [--symbols ACSA3,COFL2,...] [--seed 42]
```

- **Import** (about 30 seconds) validates `meta.xml` against the expected Taxon, Occurrence, and MeasurementOrFact columns, then hashes the three data files. An identical successful source hash is skipped unless `--force` is given. `--version` and `--zenodo-record` default to the release above and are recorded in `source_import` (source `USDA`, kind `EolTraits`). The files stream through binary COPY staging and publish in one transaction:
  - `usda_taxon` is upserted by symbol, and absent symbols become `IsCurrent = false`.
  - `usda_fact` and `usda_distribution` are replaced wholesale, with remarks and measurement-method text deduplicated into `usda_remark`.
  - `usda_wfo_link` is rebuilt.

  Missing IDs, duplicate symbols or occurrence IDs, and facts pointing at unknown occurrences fail publication. Occurrences whose taxon is missing from `taxon.tab` (18,284 in release 8) are skipped with one warning per symbol. The console and `ValidationJson` report file hashes, rows, rejections, warnings by kind, facts by trait, taxa with characteristics, unmapped and unresolved values, distribution counts (including taxa present in Ohio), link outcomes, and taxa with more than two heights.
- **Link** connects each current symbol to WFO. It uses Wikidata first: an item carrying the symbol as P1772 and a current, resolved WFO link. One distinct taxon gives `Linked`; several give `Conflict`. Otherwise it falls back to an exact normalized name at the same rank: one match gives `Linked`, several give `Ambiguous`, and none gives `NotFound`. Candidates go to `DetailJson`, and nothing is guessed. The accepted taxon follows the Wikidata rule (the taxon itself if `Accepted`, its accepted taxon if a `Synonym`). Every import relinks, and so does every `wfo` command, after Wikidata re-resolution. Run `usda link` after refreshing Wikidata.
- **Verify** compares imported labels with USDA's live labeled characteristics. The sample is deterministic and seeded, drawn from taxa with characteristics, and always includes ACSA3, COFL2, RUHI2, and ECPU. It prints agreements and disagreements per trait, inferred codes that were confirmed, unmapped codes seen next to USDA values, and shade tolerance under both hypotheses. It exits 2 if any `verified` code disagrees. It uses USDA's unofficial JSON backend (`plantsservices.sc.egov.usda.gov`) as a verification aid only; the import never depends on it. Requests are one at a time, at least 500 ms apart, with the Wikidata User-Agent (override with `Usda:UserAgent`).

**Seed files.** Readable values come from three committed, human-reviewed files under `data/reference/`. The importer makes the database tables match them exactly on every run, even when the data import is skipped, and prints how many rows changed.

| File | Contents |
| --- | --- |
| `usda-trait-types.csv` | Each `measurementType` → stable `key` (e.g. `shade_tolerance`), label, and value kind (`coded`, `numeric`, `literal`, `multi`) |
| `usda-code-labels.csv` | Each value code (the URI's last segment, or the literal) → label, ordinal, `confidence` (`verified`, `inferred`, `unresolved`), and `evidence`. A row with `type_uri` set overrides the generic row for that one trait. |
| `place-labels.csv` | GeoNames and Wikidata place IDs → name, kind, country and admin codes. Regenerate it with `tools/generate-place-labels.cs` from GeoNames' `admin1CodesASCII.txt` and `countryInfo.txt` (CC BY 4.0). |

To change a label, edit its row, cite what you checked in `evidence` (e.g. `usda verify 2026-09-26: 12 taxa agree, 0 disagree`), and rerun `usda`. Mark a label `verified` only when evidence supports it; `usda verify` lists inferred codes it confirmed. The view `reference.usda_fact_labeled` joins facts to trait keys and labels for inspection, e.g. `SELECT trait_key, value_display FROM reference.usda_fact_labeled WHERE symbol = 'ACSA3'`.

**Known quirks** (details in [USDA validation](docs/usda-validation.md)):

- `FamilyUsda` uses USDA's older family circumscriptions (e.g. *Aceraceae*). Never use it as the family.
- **Height:** a taxon has one or two `height_ft` facts. The spec assumed mature height is the larger one, but it is not always: select it by `StatisticalMethod` SIO_001110 (mature) rather than SIO_001114 (maximum at 20 years).
- **Shade tolerance is unresolved.** The archive's shade codes and USDA's live service disagree in direction on every Low/High value. Independent sources support the archive read directly (the same way as every other tolerance trait). Until a human decides and seeds labels, `shade_tolerance` shows the raw code as `unresolved`, and nothing may present sun or shade from USDA.
- **Ohio-native caveat:** USDA gives state *presence* plus native status for regions (Lower 48, Alaska, Hawaii, Canada, …), not per-state native status. "Present in Ohio and native to the contiguous US" does not mean native to Ohio.
- GeoNames 614540 in `Present` is the country of Georgia, standing in for the US state. The row's remark says "Georgia".
- Measurement and occurrence IDs are positional and are never used as durable keys.

## Wikipedia descriptions

English Wikipedia is the first description source. For every current Wikidata item with an English Wikipedia sitelink (about 90,000), the importer stages the article's **lead section as plain text** and Wikipedia's **short description**, with the metadata needed to attribute, license, refresh, and trace them. This is staging only: choosing which description a plant shows belongs to the catalog. Import Wikidata details first (they supply the sitelinks), then run:

```sh
dotnet run --project src/AnythingCanBeFarming.DataImport -- wikipedia leads [--full] [--limit N]
dotnet run --project src/AnythingCanBeFarming.DataImport -- wikidata all --with-wikipedia [--allow-shrink] [--full] [--limit N]
```

`--with-wikipedia` runs the leads import as the last step of `wikidata all`, with the same `--full` and `--limit`; it is off by default. Every run records a `reference.source_import` row (source `Wikipedia`, kind `Leads`) with its options, counts, the validation report below, and a sanitized error message. Runs serialize on the source's advisory lock, like Wikidata.

- **Selection.** Articles are chosen only by `wikidata_item.EnwikiTitle` on current items; Wikipedia is never searched by name. Each distinct title is one `reference.wikipedia_article` row (unique on `Wiki`, `RequestedTitle`), and `reference.wikipedia_item_article` mirrors which current items carry it. A title no longer carried by any current item becomes `IsCurrent = false`; articles are never deleted. New titles start as `Pending` until checked.
- **Revision check.** Titles go to the Action API in batches of 50 (`prop=info|pageprops`, `redirects=1`). Every result is mapped back to its requested title through the response's `normalized` and `redirects` arrays, never by position. Page ID, title, Wikidata item, short description, and the disambiguation flag are refreshed for every article on every run.
- **Lead fetch.** A lead is fetched (TextExtracts, `exintro`, plain text, batches of 20) only when it was never fetched, its `lastrevid` changed, or `--full` is given. `continue` is followed until the batch is complete. The extract is stored exactly as returned, with the `lastrevid` from the same response. `--limit N` stops after N articles.
- **Publishing.** Each 500 articles commit together, so an interrupted run resumes through the revision check. Only changed rows are rewritten; a `--full` refetch of an identical revision keeps its original `FetchedAt`.
- **Runtime.** The first full run takes about an hour (about 89,700 articles; one lead request per 20 articles). A routine incremental run takes about 10 minutes, because it only checks revisions (one request per 50 articles) and refetches the few that changed. Both can be stopped and rerun safely.

Requests use the Wikidata User-Agent (override with `Wikipedia:UserAgent`, or `Wikidata:UserAgent` for both), one at a time, at least 200 ms apart, with `maxlag=5` and the same `Retry-After` handling. `Wikipedia:ApiBaseAddress` exists for testing against a stub.

**Status.** Each article gets exactly one status, evaluated in this order:

| Status | Meaning |
| --- | --- |
| `Missing` | The page does not exist (or the title cannot be requested). Any previously stored text and its attribution are kept. |
| `Disambiguation` | The page is a disambiguation page. No lead is stored. |
| `ItemMismatch` | The page's Wikidata item (`WikibaseItem`) is not the item that requested it. Typically the sitelink redirects to a broader article (a species to its genus, or a synonym to the accepted name), or a monotypic genus links to its species' article. The lead is stored for inspection, but **the catalog must not use it**. |
| `EmptyLead` | The lead is empty or whitespace. |
| `Ok` | Usable. If a redirect was followed to an article about the same item, `Title` holds the target. |
| `Pending` | Selected but not yet checked (for example, beyond `--limit` or after an interrupted run). |

A redirect never sets the status by itself: it is `Ok` when the target is about the same item and `ItemMismatch` otherwise. Each run reports how many redirects it followed and how many landed on a different item.

**Licensing (CC BY-SA 4.0).** Wikipedia text is licensed under [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/), and every row carries its attribution: `Url` (the article), `LastRevId` (the revision the text came from, which leads to its author history), `License` and `LicenseUrl`, and `FetchedAt` (retrieval time). Wherever the text appears:

- Show attribution with it: credit Wikipedia, link to the article (`Url`), and name and link the license. For example: "From [Acer saccharum](https://en.wikipedia.org/wiki/Acer_saccharum) on Wikipedia, CC BY-SA 4.0."
- Do not alter the stored text. `LeadText` stays exactly as retrieved; any cleanup belongs in separate derived columns (`LeadChars`, the length, is generated by PostgreSQL). Adapted text shared with others must itself be CC BY-SA 4.0 and say that it was changed.
- Treat it as untrusted source text and never render it as HTML. A lead is whatever the article said at `LastRevId`, which can be a vandalized revision; guard it before publishing (see the validation doc).

See [Wikipedia validation](docs/wikipedia-validation.md) for status counts, lead lengths, stub counts, and the garden check.

## Stop, restart, and reset

Stop debugging (or Ctrl+C for a CLI run). Normal shutdown stops the app's containers and host processes; the named PostgreSQL volume **`acbf-postgres-data` remains**. The next launch reuses it. Keep the AppHost's generated password user secret: PostgreSQL uses the original password stored in that initialized volume.

For a container left running after an interrupted AppHost, inspect `docker ps --filter name=acbf-` and stop/remove only the exact ACBF container names shown. Do not stop unrelated projects or use a global Docker prune. Stopping/removing a container without removing its named volume preserves data.

**Intentional database reset, destructive:** first stop the AppHost and remove any stopped ACBF PostgreSQL container still referencing the volume. Then:

```sh
docker volume rm acbf-postgres-data
```

Next F5 initializes an empty `acbf` database. Do not remove the database volume for ordinary dependency or frontend problems. To reset only Angular's cached files, stop/remove its container and remove `acbf-web-node-modules` and/or `acbf-web-angular-cache` instead.

## Troubleshooting

- **API unavailable:** verify its Aspire resource is running and visit the HTTPS Swagger URL to diagnose certificate trust. The browser must trust the development certificate. The default CORS origin is exactly `http://localhost:4200`; `127.0.0.1` is a different origin.
- **Database unavailable after changing secrets:** an initialized PostgreSQL volume retains its original password. Restore that password; only intentionally reset the volume if its data is disposable.
- **Sign-in errors:** check client ID, realm, redirect URI, PKCE, and Keycloak availability. The page keeps API/database diagnostics available if the authentication check fails or times out.
- **Signed in but protected API returns 401:** check the access token's issuer and `acbf-api` audience mapper. An ID token is not an API access token.
- **Port already in use:** check for a previous ACBF AppHost/container before launching another. Adjust the documented settings together if different ports are needed.
- **Breakpoints do not bind:** launch AppHost with the IDE's Aspire integration, check the Debug build configuration, and confirm the API project process is attached. A plain CLI run has no debugger attached.

Production deployment, CI/CD, storage integrations, plant/yard application schema, and domain UI are deliberately left for later phases.
