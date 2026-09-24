using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using AnythingCanBeFarming.Api.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace AnythingCanBeFarming.Api.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public async Task Anonymous_status_is_public_and_protected_endpoint_challenges()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateHttpsClient();
        var status = await client.GetFromJsonAsync<JsonElement>("/api/status");
        Assert.Equal("ok", status.GetProperty("status").GetString());
        var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
    }

    [Fact]
    public async Task Valid_signed_token_returns_only_the_expected_user_claims()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token());
        var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(user.GetProperty("authenticated").GetBoolean());
        Assert.Equal("test-subject", user.GetProperty("subject").GetString());
        Assert.Equal("test-user", user.GetProperty("username").GetString());
        Assert.Equal("Test User", user.GetProperty("displayName").GetString());
        Assert.Equal(4, user.EnumerateObject().Count());
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("signature")]
    [InlineData("malformed")]
    public async Task Invalid_tokens_are_rejected(string defect)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token(defect));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Theory]
    [InlineData("http://localhost:4200", true)]
    [InlineData("https://untrusted.example", false)]
    public async Task Cors_only_allows_the_configured_development_origin(string origin, bool allowed)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateHttpsClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/auth/me");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");
        var response = await client.SendAsync(request);
        Assert.Equal(allowed, response.Headers.Contains("Access-Control-Allow-Origin"));
        if (allowed) Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Theory]
    [InlineData(true, HttpStatusCode.OK, "connected")]
    [InlineData(false, HttpStatusCode.ServiceUnavailable, "unavailable")]
    public async Task Database_status_uses_health_checks_without_leaking_details(
        bool healthy, HttpStatusCode status, string expected)
    {
        using var factory = new ApiFactory(healthy);
        using var client = factory.CreateHttpsClient();
        var response = await client.GetAsync("/api/status/database");
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expected, body.GetProperty("database").GetString());
        Assert.Single(body.EnumerateObject());
    }

    [Fact]
    public void Database_model_contains_no_entities()
    {
        using var factory = new ApiFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AcbfDbContext>();
        Assert.Empty(db.Model.GetEntityTypes());
    }

    [Fact]
    public async Task Real_health_check_reports_unreachable_database_without_creating_schema()
    {
        using var factory = new ApiFactory(useRealHealthCheck: true);
        using var client = factory.CreateHttpsClient();
        var response = await client.GetAsync("/api/status/database");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"database\":\"unavailable\"}", await response.Content.ReadAsStringAsync());
    }

    private sealed class ApiFactory(bool healthy = true, bool useRealHealthCheck = false)
        : WebApplicationFactory<Program>
    {
        private const string Issuer = "https://identity.example/realms/acbf-test";
        private readonly RSA rsa = RSA.Create(2048);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            foreach (var setting in new Dictionary<string, string?>
            {
                ["ConnectionStrings:acbf"] = "Host=127.0.0.1;Port=1;Database=acbf;Username=unused;Timeout=1",
                ["Authentication:Authority"] = Issuer,
                ["Authentication:Audience"] = "acbf-api",
                ["Cors:AllowedOrigins:0"] = "http://localhost:4200"
            }) builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureServices(services =>
            {
                // Replace remote discovery only. The real JWT handler still validates signatures and claims.
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    var metadata = new OpenIdConnectConfiguration { Issuer = Issuer };
                    metadata.SigningKeys.Add(new RsaSecurityKey(rsa) { KeyId = "test-key" });
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
                });
                if (!useRealHealthCheck)
                {
                    services.PostConfigure<HealthCheckServiceOptions>(options =>
                    {
                        options.Registrations.Clear();
                        options.Registrations.Add(new HealthCheckRegistration("postgres",
                            new StubHealthCheck(healthy), null, ["database"]));
                    });
                }
            });
        }

        public HttpClient CreateHttpsClient() => CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
        });

        public string Token(string? defect = null)
        {
            if (defect == "malformed") return "not-a-jwt";
            using var wrongKey = RSA.Create(2048);
            var key = new RsaSecurityKey(defect == "signature" ? wrongKey : rsa) { KeyId = "test-key" };
            var token = new JwtSecurityToken(
                issuer: defect == "issuer" ? "https://untrusted.example" : Issuer,
                audience: defect == "audience" ? "other-api" : "acbf-api",
                claims: [new Claim("sub", "test-subject"), new Claim("preferred_username", "test-user"),
                    new Claim("name", "Test User"), new Claim("private-claim", "must-not-be-returned")],
                notBefore: DateTime.UtcNow.AddHours(-1),
                expires: defect == "expired" ? DateTime.UtcNow.AddMinutes(-10) : DateTime.UtcNow.AddMinutes(5),
                signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) rsa.Dispose();
        }
    }

    private sealed class StubHealthCheck(bool healthy) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(healthy ? HealthCheckResult.Healthy() :
                HealthCheckResult.Unhealthy("Sensitive internal detail", new Exception("Do not expose this")));
    }
}
