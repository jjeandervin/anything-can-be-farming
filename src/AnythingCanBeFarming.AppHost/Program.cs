var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("acbf-postgres")
    .WithImageTag("17.9")
    .WithDataVolume("acbf-postgres-data");
var database = postgres.AddDatabase("acbf");

var authority = builder.Configuration["Authentication:Authority"]
    ?? throw new InvalidOperationException("Configure Authentication:Authority.");
var audience = builder.Configuration["Authentication:Audience"]
    ?? throw new InvalidOperationException("Configure Authentication:Audience.");
var origin = builder.Configuration["Web:Origin"]
    ?? throw new InvalidOperationException("Configure Web:Origin.");

builder.AddProject<Projects.AnythingCanBeFarming_Api>("acbf-api", launchProfileName: "https")
    .WithReference(database)
    .WaitFor(database)
    .WithEnvironment("Authentication__Authority", authority)
    .WithEnvironment("Authentication__Audience", audience)
    .WithEnvironment("Cors__AllowedOrigins__0", origin);

var webPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../../apps/web"));
builder.AddDockerfile("acbf-web", webPath, "Dockerfile.dev")
    .WithBindMount(webPath, "/app")
    .WithVolume("acbf-web-node-modules", "/app/node_modules")
    .WithVolume("acbf-web-angular-cache", "/app/.angular")
    .WithHttpEndpoint(port: new Uri(origin).Port, targetPort: 4200, isProxied: false)
    // Browser configuration needs host-facing URLs, not container-network addresses.
    .WithEnvironment("ACBF_API_BASE_URL", builder.Configuration["Web:ApiBaseUrl"]!)
    .WithEnvironment("ACBF_KEYCLOAK_ISSUER", authority)
    .WithEnvironment("ACBF_KEYCLOAK_CLIENT_ID", builder.Configuration["Web:ClientId"]!);

builder.Build().Run();
