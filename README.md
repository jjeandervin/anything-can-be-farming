# Anything Can Be Farming

ACBF's initial development foundation: Angular, an ASP.NET Core controller API, PostgreSQL, and authentication against an existing Keycloak realm. Aspire orchestrates local development only. There are no application entities, tables, migrations, or domain features yet.

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

API tests exercise the actual JWT bearer handler with locally signed test tokens, including rejection of bad issuer, audience, signature, expiry, and malformed tokens. They also check CORS, anonymous/protected endpoints, health failure responses, and the empty EF model. The remote Keycloak installation is not used or modified by tests. Frontend tests cover session restoration settings, token renewal, sign-in/out, and restricting token attachment to the API.

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

`AcbfDbContext` has no entities or DbSets. Startup and health checks do not call `EnsureCreated`, `Migrate`, or create migration metadata. PostgreSQL's own system catalogs are expected; the `acbf` public schema stays empty.

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

Production deployment, CI/CD, storage integrations, application schema, and domain UI are deliberately left for later phases.
