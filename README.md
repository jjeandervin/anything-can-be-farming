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

API tests exercise the actual JWT bearer handler with locally signed test tokens, including rejection of bad issuer, audience, signature, expiry, and malformed tokens. They also check CORS, anonymous/protected endpoints, health failure responses, and the reference-only EF model. WFO tests cover TSV parsing and optionally real PostgreSQL imports and queries (see below). The remote Keycloak installation is not used or modified by tests. Frontend tests cover session restoration settings, token renewal, sign-in/out, and restricting token attachment to the API.

With AppHost running:

```sh
curl https://localhost:7243/api/status
curl https://localhost:7243/api/status/database
curl -i https://localhost:7243/api/auth/me
```

Expected: `{"status":"ok"}`, `{"database":"connected"}`, and **401** respectively. Use `curl.exe` in Windows PowerShell if `curl` is aliased. The database endpoint uses ASP.NET Core health checks and returns **503** with `{"database":"unavailable"}` if a connection cannot be opened; it never returns connection details or exception text.

Complete the interactive checks:

1. Open the frontend. API and Database should show **Connected**.
2. Click **Sign in**, authenticate with your existing Keycloak account, and confirm **Signed in as …** and **Protected API: Verified as …**.
3. In browser Network tools, confirm `/api/auth/me` has an `Authorization: Bearer …` header and returns 200. Do not paste tokens into tickets or committed files.
4. Reload; the Keycloak SSO session should restore the signed-in state. Click **Sign out** and verify the signed-out state.
5. Edit the subtitle in `apps/web/src/app/app.html`; confirm an Angular rebuild and browser update without restarting the AppHost.
6. Hit the C# breakpoint described above while debugging the AppHost.

`AcbfDbContext` is shared by the API and importer through `AnythingCanBeFarming.Data`. It contains only WFO reference entities. Startup and health checks do not call `EnsureCreated`, `Migrate`, or import reference data. Apply migrations explicitly before using reference endpoints; an empty migrated database returns empty search results, zero statistics, and 404 for unknown taxa.

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
| `GET /api/reference/plants/search?q=acer&pageSize=20` | Case-insensitive literal substring search across scientific name, genus, and specific epithet; current records only; stable name/ID ordering |
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

The detail endpoint now returns the resolution envelope shown above instead of the previous flat response. The nested taxon retains source fields and one-level related taxa, but omits import IDs and internal relationship IDs. Search continues to query current backbone taxa only. Supplemental records are not added as separate search results.

See [supplemental validation](docs/wfo-supplemental-validation.md) for the staged-file counts and checks.

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
