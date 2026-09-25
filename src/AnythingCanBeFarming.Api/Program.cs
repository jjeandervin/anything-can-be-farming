using AnythingCanBeFarming.Data;
using AnythingCanBeFarming.Api.Health;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("acbf")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:acbf or launch through the AppHost.");
var authority = builder.Configuration["Authentication:Authority"]
    ?? throw new InvalidOperationException("Configure Authentication:Authority.");
var audience = builder.Configuration["Authentication:Audience"]
    ?? throw new InvalidOperationException("Configure Authentication:Audience.");

builder.Services.AddControllers();
builder.Services.AddDbContext<AcbfDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddHealthChecks().AddCheck<PostgresHealthCheck>("postgres", tags: ["database"]);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Authority = authority;
    options.Audience = audience;
    options.RequireHttpsMetadata = true;
    options.MapInboundClaims = false;
    options.TokenValidationParameters.NameClaimType = "preferred_username";
    options.TokenValidationParameters.ValidateIssuer = true;
    options.TokenValidationParameters.ValidateAudience = true;
    options.TokenValidationParameters.ValidateLifetime = true;
    options.TokenValidationParameters.ValidateIssuerSigningKey = true;
});
builder.Services.AddAuthorization();

if (builder.Environment.IsDevelopment())
{
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    builder.Services.AddCors(options => options.AddPolicy("Development", policy =>
        policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
}

var app = builder.Build();
app.UseHttpsRedirection();
if (app.Environment.IsDevelopment())
{
    app.UseCors("Development");
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();

// Exposes the entry point to the integration test host.
public partial class Program;
