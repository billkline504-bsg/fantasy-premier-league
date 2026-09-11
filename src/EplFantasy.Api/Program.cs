using System.IdentityModel.Tokens.Jwt;
using System.Text;
using EplFantasy.Api.Authentication;
using EplFantasy.Infrastructure;
using EplFantasy.Infrastructure.Authentication;
using EplFantasy.Infrastructure.Authorization;
using EplFantasy.Infrastructure.Correlation;
using EplFantasy.Infrastructure.ErrorHandling;
using EplFantasy.Infrastructure.RateLimiting;
using EplFantasy.SharedKernel;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Keep the "sub" claim under its original JWT name instead of ASP.NET Core's legacy inbound
// remapping (which would otherwise rename it to a long .NET/WS-Fed claim URI) — see
// HttpContextCurrentUserAccessor's remarks.
JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException($"Missing required configuration section \"{JwtOptions.SectionName}\".");

// Add services to the container.
builder.Services.AddControllers();

builder.Services.AddInfrastructure(builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Missing required connection string \"Default\"."));
builder.Services.AddAuthenticationInfrastructure(builder.Configuration);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserAccessor, HttpContextCurrentUserAccessor>();

// Bearer JWT authentication (ADR-006, Architecture §9.1) — validates against the same JwtOptions
// JwtTokenFactory issues tokens with. Authenticates the caller only ("who is this"); the
// object-level "is this caller allowed to touch this specific League/FantasyTeam" checks AP-002
// requires are the policies registered below.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });

// IT-F06: the shared authorization-handler pipeline (Architecture §9.3) — every controller
// action declares which of these policies it needs; no action re-implements the check inline.
builder.Services.AddEplFantasyAuthorizationPolicies();

// IT-F08 (ADR-012): the shared deadline-sweep loop. IT-27 (F-005.4, BR-282) registers the
// draft-pick-timeout handler; IT-31 (F-007.3, BR-093-BR-096/BR-305) registers the Gameweek
// roster-lock/carry-forward handler; IT-55 (F-012.2, BR-152/BR-338) registers the Gameweek
// reminder handler that fires before that same deadline is ever reached.
builder.Services.AddDeadlineSweep(builder.Configuration);
builder.Services.AddScoped<IDeadlineSweepHandler, DraftPickTimeoutSweepHandler>();
builder.Services.AddScoped<IDeadlineSweepHandler, RosterLockSweepHandler>();
builder.Services.AddScoped<IDeadlineSweepHandler, GameweekReminderSweepHandler>();

// IT-F12 (Architecture §12.2): the outbox-draining loop. No INotificationSender is registered
// yet — the email/SMS provider is still an open decision (Architecture §15); until F-012.4 picks
// one, every eligible request just retries with backoff and never actually sends.
builder.Services.AddNotificationOutbox(builder.Configuration);

// IT-F14 (Architecture §9.3): ProblemDetails error shaping (errorCode + correlationId on every
// error response) and the "auth" rate-limiting policy (§11, BR-169) feeding security_events.
builder.Services.AddEplFantasyProblemDetails();
builder.Services.AddEplFantasyRateLimiting(builder.Configuration);

var app = builder.Build();

// Configure the HTTP request pipeline. UseExceptionHandler/UseStatusCodePages are registered
// first so they wrap (and can shape a ProblemDetails response for) everything after them —
// including a thrown exception from CorrelationIdMiddleware itself, or a bare 401/403/404/429
// from routing, auth, or the rate limiter that never throws at all.
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCorrelationId();
app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// Confirms the host boots and Architecture §9.1's URL-path versioning convention (/api/v1/...) is
// wired from the start; every controller a feature task adds should route under the same
// "api/v1/[controller]" prefix.
app.MapGet("/api/v1/health", () => Results.Ok(new { status = "ok" }));

app.Run();

// Exposed for EplFantasy.ApiTests' WebApplicationFactory<Program> (Testing Strategy v1.0 §1).
public partial class Program;
