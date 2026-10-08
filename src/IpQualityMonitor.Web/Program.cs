using System.Security.Claims;
using System.Net;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using IpQualityMonitor.Application;
using IpQualityMonitor.Linux;
using IpQualityMonitor.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using TcpLatencyMonitor.Core;

// The health-check entry point MUST NOT construct a second runtime or open the database.
if (args.Length == 1 && args[0] == "--healthcheck")
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var response = await client.GetAsync("http://127.0.0.1:8080/health/ready");
        Environment.ExitCode = response.IsSuccessStatusCode ? 0 : 1;
    }
    catch { Environment.ExitCode = 1; }
    return;
}
if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("此宿主面向 Linux；Windows 版使用原 App 项目。");
var builder = WebApplication.CreateBuilder(args);
var data = Path.GetFullPath(builder.Configuration["IPQUALITY_DATA_DIR"] ?? "/data");
var zone = TimeZoneInfo.FindSystemTimeZoneById(builder.Configuration["IPQUALITY_TIME_ZONE"] ?? "Asia/Shanghai");
var secureCookieSetting = builder.Configuration["IPQUALITY_SECURE_COOKIES"] ?? "false";
if (!bool.TryParse(secureCookieSetting, out var secureCookies))
    throw new InvalidOperationException("IPQUALITY_SECURE_COOKIES 必须为 true 或 false。");
builder.WebHost.UseUrls(builder.Configuration["ASPNETCORE_URLS"] ?? "http://0.0.0.0:8080");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 32768);
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(40));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.AddDataProtection().SetApplicationName("IpQualityMonitor.Server.v1")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(data, "keys")));
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 1;
    // Keep framework loopback defaults; add explicit trusted proxy IPs, never trust arbitrary senders.
    foreach (var proxy in (builder.Configuration["IPQUALITY_TRUSTED_PROXIES"] ?? "")
                 .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        o.KnownProxies.Add(IPAddress.Parse(proxy));
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "iqm.session"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromHours(8); o.SlidingExpiration = true;
    o.Events.OnValidatePrincipal = async c =>
    {
        var admin = c.HttpContext.RequestServices.GetRequiredService<AdminCredentials>();
        if (c.Principal?.FindFirst("iqm.auth-version")?.Value != admin.SecurityStamp)
        {
            c.RejectPrincipal();
            await c.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    };
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.Name = "iqm.csrf"; o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", _ => RateLimitPartition.GetFixedWindowLimiter("single-admin-login", _ =>
        new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("api", _ => RateLimitPartition.GetFixedWindowLimiter("api", _ =>
        new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddSingleton(sp => new ServerStorage(data, builder.Configuration["IPQUALITY_SITE_NAME"] ?? "家庭 NAS · Docker bridge"));
builder.Services.AddSingleton<AdminCredentials>();
builder.Services.AddSingleton(sp => new MtrPacketClient(builder.Configuration["IPQUALITY_MTR_PATH"] ?? "/usr/local/libexec/iqm-mtr-packet"));
builder.Services.AddSingleton(sp => new LinuxNetworkContext(builder.Configuration["IPQUALITY_IP_PATH"] ?? "/usr/sbin/ip",
    sp.GetRequiredService<ServerStorage>().Site.Id));
builder.Services.AddSingleton(sp =>
{
    var packets = sp.GetRequiredService<MtrPacketClient>();
    var network = sp.GetRequiredService<LinuxNetworkContext>();
    var hop = new LinuxHopProbe(packets);
    var resources = new MonitorResources(32,
        probes: target => target.Protocol == ProbeProtocol.Tcp ? new TcpProbe() : new LinuxIcmpProbe(packets),
        contexts: network.Read,
        routeProbe: async (target, context, reason, options, token, progress) =>
        {
            var before = await network.RefreshAsync(target.Address, token);
            if (before.Key == "unknown" || before.Key != context) throw new OperationCanceledException("容器选路改变或尚未确定。");
            var result = await new RouteProbe(hop).RunAsync(target, context, reason, options, token, progress);
            var after = await network.RefreshAsync(target.Address, token);
            if (before.Key != after.Key) throw new OperationCanceledException("路由探测期间容器选路改变。");
            return result with { ContextDescription = before.Description };
        });
    return new MonitorRuntime(sp.GetRequiredService<ServerStorage>(), resources);
});
builder.Services.AddSingleton<SharedTimelineReader>();
builder.Services.AddHostedService<MonitoringHost>();
await using var app = builder.Build();
// Fail closed before listening when first-run credentials are missing or invalid.
_ = app.Services.GetRequiredService<AdminCredentials>();
app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store";
    try { await next(context); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception ex)
    {
        if (context.Response.HasStarted) throw;
        var status = ex switch
        {
            BadHttpRequestException requestError => requestError.StatusCode,
            ConfigurationConflictException => 409,
            KeyNotFoundException => 404,
            ArgumentException or AntiforgeryValidationException => 400,
            InvalidOperationException => 503,
            _ => 500
        };
        if (status == 500) app.Logger.LogError(ex, "Server operation failed");
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = status == 500 ? "服务器内部错误，请检查服务日志。" : ex.Message });
    }
});
app.UseRouting(); app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") &&
        context.Request.Method is not ("GET" or "HEAD" or "OPTIONS"))
        await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
    await next(context);
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" })).AllowAnonymous();
app.MapGet("/health/ready", (MonitorRuntime runtime) => runtime.Ready && runtime.History.WriteFailure is null ? Results.Ok() : Results.StatusCode(503)).AllowAnonymous();
app.MapGet("/api/auth/csrf", (HttpContext context, IAntiforgery antiforgery) =>
    Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken })).AllowAnonymous();
app.MapGet("/api/auth/session", (HttpContext context) => Results.Ok(new { authenticated = context.User.Identity?.IsAuthenticated == true })).AllowAnonymous();
app.MapPost("/api/auth/login", async (LoginInput input, AdminCredentials admin, HttpContext context) =>
{
    if (!admin.Verify(input.Username, input.Password)) return Results.Unauthorized();
    var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "admin"),
        new Claim(ClaimTypes.Name, admin.Username), new Claim("iqm.auth-version", admin.SecurityStamp) }, CookieAuthenticationDefaults.AuthenticationScheme));
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
    return Results.NoContent();
}).AllowAnonymous().RequireRateLimiting("login");
var api = app.MapGroup("/api").RequireAuthorization().RequireRateLimiting("api");
api.MapPost("/auth/logout", async (HttpContext context) =>
{ await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); return Results.NoContent(); });
api.MapGet("/overview", (MonitorRuntime runtime, MtrPacketClient packets) => Results.Ok(new
{
    site = runtime.Site, config = runtime.Configuration, targets = runtime.States(),
    serverTime = DateTimeOffset.UtcNow, timeZone = zone.Id, error = runtime.Error,
    analysisError = runtime.AnalysisError, probeError = packets.LastError, pendingProbes = packets.Pending,
    network = "bridge", preview = true
}));
api.MapPost("/targets", async (AddTargetInput input, MonitorRuntime runtime) =>
    Results.Ok(new { id = await runtime.AddAsync(input.Target, input.Revision) }));
api.MapPost("/targets/{id}/run", async (string id, RunningInput input, MonitorRuntime runtime,
    LinuxNetworkContext network, HttpContext context) =>
{
    if (input.Running) await network.RefreshAsync(runtime.RouteTarget(id).Address, context.RequestAborted);
    await runtime.SetRunningAsync(id, input.Running, input.Revision); return Results.NoContent();
});
api.MapDelete("/targets/{id}", async (string id, long revision, MonitorRuntime runtime) =>
{ await runtime.RemoveAsync(id, revision); return Results.NoContent(); });
api.MapPut("/settings", async (PolicyInput input, MonitorRuntime runtime) =>
{ await runtime.SetPolicyAsync(input.Monitoring, input.Revision); return Results.NoContent(); });
api.MapPost("/targets/{id}/route", (string id, MonitorRuntime runtime) =>
    runtime.RequestRoute(id) ? Results.Accepted() : Results.Conflict(new { error = "目标未运行或请求未被接受。" }));
api.MapGet("/targets/{id}/timeline", async (string id, string protocol, int days, MonitorRuntime runtime,
    SharedTimelineReader timelines, HttpContext context) =>
{
    if (!Enum.TryParse<ProbeProtocol>(protocol, true, out var parsed) || !Enum.IsDefined(parsed))
        throw new ArgumentException("未知的协议。");
    var timeline = await timelines.ReadAsync(runtime.Resolve(id, parsed), days, zone, context.RequestAborted);
    return Results.Ok(new { timeline, timeZone = zone.Id });
});
api.MapGet("/targets/{id}/routes", async (string id, MonitorRuntime runtime, HttpContext context) =>
    Results.Ok(await runtime.QueryAsync(h => h.LoadRoutes(runtime.RouteTarget(id), 20), context.RequestAborted)));
api.MapGet("/targets/{id}/events", async (string id, MonitorRuntime runtime, HttpContext context) =>
    Results.Ok(await runtime.QueryAsync(h => h.LoadEvents(runtime.RouteTarget(id), 100), context.RequestAborted)));
await app.RunAsync();

internal sealed record LoginInput([property: JsonRequired] string? Username, [property: JsonRequired] string? Password);
internal sealed record AddTargetInput([property: JsonRequired] long Revision, [property: JsonRequired] TargetInput Target);
internal sealed record RunningInput([property: JsonRequired] long Revision, [property: JsonRequired] bool Running);
internal sealed record PolicyInput([property: JsonRequired] long Revision, [property: JsonRequired] GlobalMonitorSettings Monitoring);
