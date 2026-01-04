using System;
using System.IO;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Model;
using Server;
using static FCQRS.Common;
using static FCQRS.CSharp;
using CID = FCQRS.Model.Data.CID;
using IMessageWithCID = FCQRS.Model.Data.IMessageWithCID;

var logf = LoggerFactory.Create(x => x.AddConsole());

var dbPath = Environment.GetEnvironmentVariable("FOCUMENT_DB_PATH") ?? "focument_csharp.db";
var connectionString = $"Data Source={dbPath};";

CID GetCid() => Helpers.NewCID();

// Initialize projection tables
Projection.EnsureTables(connectionString);

// Initialize handlers logging
Handlers.SetLogger(logf);

var builder = WebApplication.CreateBuilder(args);

// =============================================================================
// SECURITY SERVICES CONFIGURATION
// =============================================================================

// Configure forwarded headers for running behind nginx ingress
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Antiforgery for CSRF protection
builder.Services.AddAntiforgery();

// Form size limits (2000 chars per field)
builder.Services.Configure<FormOptions>(options =>
{
    options.ValueLengthLimit = 2000;
    options.MultipartBodyLengthLimit = 8192;
});

// Rate limiting per-IP: 30 requests per minute (sliding window)
// This effectively enforces the strictest tier of the multi-tier limits
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("WritePolicy", context =>
        RateLimitPartition.GetSlidingWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                AutoReplenishment = true
            }));
});

var actorApi = ActorApi.Create(
    builder.Configuration,
    logf,
    connectionString,
    "FocumentCluster");

// Initialize the approval saga
var sagaFactory = DocumentApprovalSaga.Factory(actorApi);

// Initialize saga starter - triggers saga when document is created
IActorExtensions.InitSagaStarter(actorApi, evt =>
{
    // When a document is created, start the approval saga
    if (evt is Event<DocumentEvent> { EventDetails: DocumentEvent.CreatedOrUpdated })
    {
        return
        [
            new SagaDefinition
            {
                Factory = sagaFactory,
                PrefixConversion = PrefixConversions.Identity,
                StartingEvent = evt
            }
        ];
    }
    return [];
});

// Initialize projection subscription
var lastOffset = ServerQuery.GetLastOffset(connectionString);

var subs = QueryApi.InitWithList(
    actorApi,
    (int)lastOffset,
    (offset, evt) => Projection.HandleEventWrapper(logf, connectionString, offset, evt));

var commandHandler = ICommandHandlers.Create(actorApi);

var app = builder.Build();

// =============================================================================
// FORWARDED HEADERS (must be first for correct client IP behind nginx)
// =============================================================================
app.UseForwardedHeaders();

// =============================================================================
// PATH BASE CONFIGURATION (for subpath deployment like /focument-fsharp)
// =============================================================================
var pathBase = Environment.GetEnvironmentVariable("ASPNETCORE_PATHBASE");
if (!string.IsNullOrEmpty(pathBase))
{
    app.UsePathBase(pathBase);

}

// =============================================================================
// SECURITY MIDDLEWARE PIPELINE
// =============================================================================

// HTTPS enforcement
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
app.UseHttpsRedirection();

// Security headers
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;

    // Global headers for all responses
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";

    await next();

    // Add CSP only for HTML responses
    var contentType = context.Response.ContentType;
    if (contentType != null && contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
    {
        headers["Content-Security-Policy"] =
            "default-src 'self'; " +
            "script-src 'self'; " +
            "style-src 'self'; " +
            "img-src 'self' data:; " +
            "font-src 'self'; " +
            "connect-src 'self'; " +
            "frame-ancestors 'none'; " +
            "base-uri 'self'; " +
            "form-action 'self'";
    }
});

// Rate limiting
app.UseRateLimiter();

// Routing and antiforgery
app.UseRouting();
app.UseAntiforgery();

// Static files (CSS, JS, etc.)
app.UseStaticFiles();

// Serve index.html with server-side base tag injection
var indexHtmlPath = Path.Combine(app.Environment.WebRootPath, "index.html");
var indexHtmlTemplate = File.ReadAllText(indexHtmlPath);

app.MapGet("/", (HttpContext ctx) =>
{
    var basePath = (ctx.Request.PathBase.Value ?? "") + "/";
    var html = indexHtmlTemplate.Replace("{{BASE}}", basePath);
    return Microsoft.AspNetCore.Http.Results.Content(html, "text/html");
});

app.MapGet("/index.html", (HttpContext ctx) =>
{
    var basePath = (ctx.Request.PathBase.Value ?? "") + "/";
    var html = indexHtmlTemplate.Replace("{{BASE}}", basePath);
    return Microsoft.AspNetCore.Http.Results.Content(html, "text/html");
});

app.MapGet("/api/documents", () => Handlers.GetDocuments(connectionString));

app.MapGet("/api/test", () => "Hello from test!");

// Antiforgery token endpoint
app.MapGet("/api/antiforgery-token", (IAntiforgery antiforgery, HttpContext context) =>
{
    var tokens = antiforgery.GetAndStoreTokens(context);
    return Microsoft.AspNetCore.Http.Results.Ok(new { token = tokens.RequestToken, headerName = tokens.HeaderName });
});

app.MapGet("/api/document/{id}/history", (HttpContext ctx) =>
    Handlers.GetDocumentHistory(connectionString, ctx));

app.MapPost("/api/document", async (HttpContext ctx) =>
    Microsoft.AspNetCore.Http.Results.Text(await Handlers.CreateOrUpdateDocument(GetCid, subs, commandHandler, ctx)))
    .RequireRateLimiting("WritePolicy");

app.MapPost("/api/document/restore", async (HttpContext ctx) =>
    Microsoft.AspNetCore.Http.Results.Text(await Handlers.RestoreVersion(connectionString, GetCid, subs, commandHandler, ctx)))
    .RequireRateLimiting("WritePolicy");

app.Run();
