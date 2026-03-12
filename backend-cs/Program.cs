using DriveChill.Hardware;
using DriveChill.Services;
using Prometheus;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DriveChill;

/// <summary>
/// Application entry point.
///
/// Threading model:
///   Main thread  →  Windows Forms Application.Run() + NotifyIcon message pump
///   Task.Run()   →  ASP.NET Core IHost (Kestrel + background services)
///
/// NotifyIcon requires STA + main thread ownership of the Win32 message pump.
/// </summary>
internal static class Program
{
    private static readonly string[] _publicApiPaths =
    [
        "/api/health",
        "/api/ws",
        "/api/auth/login",
        "/api/auth/setup",
        "/api/auth/status",
        "/api/auth/session",
    ];
    private static readonly (string Prefix, string Domain)[] _apiKeyScopePrefixRules =
    [
        ("/api/auth/api-keys", "auth"),
        ("/api/alerts", "alerts"),
        ("/api/drives", "drives"),
        ("/api/fans", "fans"),
        ("/api/machines", "machines"),
        ("/api/notifications", "notifications"),
        ("/api/profiles", "profiles"),
        ("/api/quiet-hours", "quiet_hours"),
        ("/api/sensors", "sensors"),
        ("/api/settings", "settings"),
        ("/api/webhooks", "webhooks"),
        ("/api/analytics", "analytics"),
        ("/api/temperature-targets", "temperature_targets"),
        ("/api/virtual-sensors", "virtual_sensors"),
        ("/api/notification-channels", "notifications"),
        ("/api/profile-schedules", "profiles"),
        ("/api/noise-profiles", "settings"),
        ("/api/report-schedules", "settings"),
        ("/api/scheduler", "settings"),
        ("/api/annotations", "analytics"),
        ("/api/integrations", "settings"),
        ("/api/update", "settings"),
    ];
    private static readonly HashSet<string> _readMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "HEAD", "OPTIONS",
    };

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        var settings = new AppSettings();
        var listenUrl = BuildListenUrl(settings.Host, settings.Port);
        var dashboardUrl = $"http://localhost:{settings.Port}";

        // Build ASP.NET Core host
        var host = BuildHost(args, settings, listenUrl);

        // Auth guard: when binding to non-localhost, refuse to start if no
        // users exist and DRIVECHILL_PASSWORD is not set. Matches Python backend.
        if (settings.AuthRequired)
        {
            var db = host.Services.GetRequiredService<DbService>();
            var hasUser = db.UserExistsAsync().GetAwaiter().GetResult();
            if (!hasUser && settings.Password is null)
            {
                Console.Error.WriteLine(
                    "ERROR: Session auth required for non-localhost binding " +
                    $"(host={settings.Host}). Set DRIVECHILL_PASSWORD environment " +
                    "variable before starting so the admin user can be created automatically.");
                Environment.Exit(1);
            }
            if (!hasUser && settings.Password is not null)
            {
                var sessions = host.Services.GetRequiredService<SessionService>();
                sessions.SetupAsync("admin", settings.Password).GetAwaiter().GetResult();
                Console.WriteLine("Created admin user from DRIVECHILL_PASSWORD env var");
                db.LogAuthEventAsync("user_created", "localhost", "admin", "success",
                    "Auto-created from DRIVECHILL_PASSWORD env var").GetAwaiter().GetResult();
            }
        }

        using var cts = new CancellationTokenSource();

        // Start host on a background Task
        var hostTask = Task.Run(async () =>
        {
            try
            {
                await host.StartAsync(cts.Token);
                await host.WaitForShutdownAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
        });

        // Auto-open browser after Kestrel has had time to bind
        Task.Run(async () =>
        {
            await Task.Delay(1800);
            try { Process.Start(new ProcessStartInfo(dashboardUrl) { UseShellExecute = true }); }
            catch { }
        });

        // Build tray icon — runs on main thread, blocks until Quit
        using var trayIcon = BuildTrayIcon(dashboardUrl, () =>
        {
            cts.Cancel();
            try { hostTask.GetAwaiter().GetResult(); } catch { }
            Application.ExitThread();
        });

        trayIcon.Visible = true;
        Application.Run();       // blocks here

        trayIcon.Visible = false;
        host.Dispose();
    }

    // -----------------------------------------------------------------------
    // ASP.NET Core host
    // -----------------------------------------------------------------------

    private static IHost BuildHost(string[] args, AppSettings settings, string listenUrl)
    {
        // Point wwwroot at the folder next to the EXE — works for both
        // 'dotnet run' (where CWD = project dir) and self-contained publish.
        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args           = args,
            WebRootPath    = wwwroot,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseUrls(listenUrl);

        // HTTPS/TLS configuration — applied when SSL env vars are set
        if (settings.SslCertFile is not null && settings.SslKeyFile is not null)
        {
            builder.WebHost.ConfigureKestrel(serverOptions =>
            {
                serverOptions.ConfigureHttpsDefaults(httpsOptions =>
                {
                    httpsOptions.ServerCertificate =
                        System.Security.Cryptography.X509Certificates.X509Certificate2
                            .CreateFromPemFile(settings.SslCertFile, settings.SslKeyFile);
                });
            });
            // Re-register URL as https://
            builder.WebHost.UseUrls(listenUrl.Replace("http://", "https://"));
        }

        // Suppress default console logging in tray mode
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // JSON: use snake_case everywhere to match the TypeScript frontend contract
        var jsonOpts = new Action<System.Text.Json.JsonSerializerOptions>(o =>
            o.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower);

        builder.Services.ConfigureHttpJsonOptions(o => jsonOpts(o.SerializerOptions));
        builder.Services.AddControllers().AddJsonOptions(o => jsonOpts(o.JsonSerializerOptions));

        // App settings singleton
        builder.Services.AddSingleton(settings);

        // Hardware backend: set env var DRIVECHILL_BACKEND=mock for dev/testing
        var isMock = Environment.GetEnvironmentVariable("DRIVECHILL_BACKEND") == "mock";
        builder.Services.AddSingleton<IHardwareBackend>(_ =>
            isMock ? (IHardwareBackend)new MockBackend() : new LhmBackend());

        // Drive provider: smartctl-based by default; mock for testing/dev
        if (isMock)
            builder.Services.AddSingleton<IDriveProvider, MockDriveProvider>();
        else
            builder.Services.AddSingleton<IDriveProvider, SmartctlDriveProvider>();

        // Core services
        builder.Services.AddSingleton<SensorService>();
        builder.Services.AddSingleton<TemperatureTargetService>();
        builder.Services.AddSingleton<VirtualSensorService>();
        builder.Services.AddSingleton<FanService>(sp =>
            new FanService(
                sp.GetRequiredService<IHardwareBackend>(),
                sp.GetRequiredService<SettingsStore>(),
                sp.GetRequiredService<AppSettings>(),
                sp.GetRequiredService<TemperatureTargetService>(),
                sp.GetRequiredService<VirtualSensorService>()));
        builder.Services.AddSingleton<AlertService>(sp =>
            new AlertService(sp.GetRequiredService<DbService>()));
        builder.Services.AddSingleton<AlertDeliveryService>();
        builder.Services.AddSingleton<DbService>();
        builder.Services.AddSingleton<SettingsStore>();
        builder.Services.AddSingleton<ApiKeyService>();
        builder.Services.AddSingleton<SessionService>();
        builder.Services.AddSingleton<WebhookService>();
        builder.Services.AddSingleton<EmailNotificationService>();
        builder.Services.AddSingleton<PushNotificationService>();
        builder.Services.AddSingleton<FanTestService>();
        builder.Services.AddSingleton<SmartTrendService>();
        builder.Services.AddSingleton<NotificationChannelService>();
        builder.Services.AddSingleton<DriveMonitorService>();
        builder.Services.AddSingleton<WebSocketHub>();
        builder.Services
            .AddHttpClient("webhooks")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
            });
        // Note: MachinesController creates its own SocketsHttpHandler per request
        // to lock DNS resolution and prevent DNS rebinding. No shared "machines" client needed.

        // Background worker: polls hardware, broadcasts WebSocket messages
        builder.Services.AddHostedService<SensorWorker>();
        // Background worker: drive monitoring (polls smartctl, publishes hdd_temp sensors)
        builder.Services.AddHostedService<DriveMonitorWorker>();
        // Background worker: subscribes to MQTT command topics for external control
        builder.Services.AddHostedService<MqttCommandHandler>();
        // Background worker: profile scheduling (time-of-day profile switching)
        // Registered as singleton + hosted service so the status API can read state.
        builder.Services.AddSingleton<ProfileSchedulerService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ProfileSchedulerService>());
        // Background worker: scheduled analytics report emails
        builder.Services.AddSingleton<ReportSchedulerService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ReportSchedulerService>());

        // CORS for Next.js dev server.
        // AllowCredentials() is required for session cookies to be forwarded on
        // cross-origin requests from the dev server (port 3000).
        builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
            p.WithOrigins("http://localhost:3000", "http://localhost:8085")
             .AllowAnyMethod().AllowAnyHeader().AllowCredentials()));

        var app = builder.Build();

        app.UseCors();

        // Prometheus metrics endpoint — auth-exempt (scrapers use network-level isolation).
        // Gated by DRIVECHILL_PROMETHEUS_ENABLED=true.
        if (settings.PrometheusEnabled)
            app.UseMetricServer("/metrics");

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        // Security response headers
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            // Explicitly allow ws:// and wss:// so the WebSocket connection works
            // even in browsers where 'self' alone does not cover the ws/wss
            // scheme mapping.  Use the request Host so CSP works for any
            // deployment (not just localhost).  This is safe: CSP is a browser-side
            // directive and the Host header reflects the origin the browser used.
            var wsHost = context.Request.Host.HasValue
                ? context.Request.Host.ToString()
                : $"localhost:{settings.Port}";
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'self'; " +
                // Next.js static export injects inline bootstrap scripts.
                "script-src 'self' 'unsafe-inline'; " +
                // Google Fonts stylesheet loaded by the Next.js layout.
                "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
                "img-src 'self' data:; " +
                $"connect-src 'self' ws://{wsHost} wss://{wsHost}; " +
                // Google Fonts font files.
                "font-src 'self' https://fonts.gstatic.com; " +
                "frame-ancestors 'none'";
            await next();
        });

        // Serve Next.js static export from wwwroot/
        app.UseDefaultFiles();
        app.UseStaticFiles();

        // REST API auth/CSRF middleware (parity with Python backend):
        // - Auth required only when host is non-localhost (or force-auth enabled)
        // - API key auth supports scoped access
        // - Session auth requires CSRF token on state-changing requests
        app.Use(async (context, next) =>
        {
            if (!IsApiRequest(context.Request.Path) || IsPublicApiPath(context.Request.Path))
            {
                await next();
                return;
            }

            if (!settings.AuthRequired)
            {
                await next();
                return;
            }

            var apiKey = ExtractApiKey(context.Request);
            if (!string.IsNullOrEmpty(apiKey))
            {
                var apiKeys = context.RequestServices.GetRequiredService<ApiKeyService>();
                var keyMeta = apiKeys.Validate(apiKey);
                if (keyMeta is null)
                {
                    await Reject(context, 401, "Invalid API key");
                    return;
                }

                var requiredScope = RequiredApiKeyScope(context.Request);
                if (requiredScope is null || !ApiKeyHasScope(keyMeta.Scopes, requiredScope))
                {
                    await Reject(
                        context,
                        403,
                        requiredScope is null
                            ? "API key not allowed for this endpoint"
                            : $"API key missing required scope: {requiredScope}"
                    );
                    return;
                }

                // Viewer-role API keys cannot perform write operations.
                if (!_readMethods.Contains(context.Request.Method))
                {
                    var requestPath = context.Request.Path.Value ?? "";
                    if (keyMeta.Role != "admin" && requestPath is not "/api/auth/logout" and not "/api/auth/me/password")
                    {
                        await Reject(context, 403, "Write access requires admin role");
                        return;
                    }
                }

                await next();
                return;
            }

            var sessionToken = context.Request.Cookies["drivechill_session"];
            var sessions = context.RequestServices.GetRequiredService<SessionService>();
            var session = await sessions.ValidateSessionAsync(sessionToken, context.RequestAborted);
            if (session is null)
            {
                await Reject(context, 401, "Authentication required");
                return;
            }

            if (!_readMethods.Contains(context.Request.Method))
            {
                var csrfHeader = context.Request.Headers["X-CSRF-Token"].FirstOrDefault();
                if (
                    string.IsNullOrEmpty(csrfHeader)
                    || !SecureEquals(csrfHeader, session.Value.CsrfToken)
                )
                {
                    await Reject(context, 403, "CSRF token invalid or missing");
                    return;
                }

                // Viewer-role sessions are read-only.
                // Logout is exempt so viewers can always end their own session.
                var requestPath = context.Request.Path.Value ?? "";
                if (session.Value.Role != "admin" && requestPath is not "/api/auth/logout" and not "/api/auth/me/password")
                {
                    await Reject(context, 403, "Write access requires admin role");
                    return;
                }
            }

            await next();
        });

        // One-time migration: move profiles and alert rules from settings.json to SQLite
        MigrateProfilesAndAlertsToDb(app.Services).GetAwaiter().GetResult();

        app.MapControllers();

        // WebSocket endpoint auth parity with Python:
        // when auth is required, enforce a valid session cookie.
        app.Map("/api/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            if (settings.AuthRequired)
            {
                var sessionToken = context.Request.Cookies["drivechill_session"];
                var sessions = context.RequestServices.GetRequiredService<SessionService>();
                var session = await sessions.ValidateSessionAsync(sessionToken, context.RequestAborted);
                if (session is null)
                {
                    context.Response.StatusCode = 401;
                    return;
                }
            }

            await context.RequestServices.GetRequiredService<WebSocketHub>().HandleAsync(context);
        });

        return app;
    }

    /// <summary>
    /// One-time migration: if profiles/alert_rules exist in settings.json but not in SQLite,
    /// copy them to the database.
    /// </summary>
    private static async Task MigrateProfilesAndAlertsToDb(IServiceProvider services)
    {
        var db    = services.GetRequiredService<DbService>();
        var store = services.GetRequiredService<SettingsStore>();

        var existingProfiles = await db.ListProfilesAsync();
        if (existingProfiles.Count == 0)
        {
            var jsonProfiles = store.LoadProfiles();
            foreach (var p in jsonProfiles)
                await db.CreateProfileAsync(p);

            var jsonAlerts = store.LoadAlerts();
            foreach (var a in jsonAlerts)
                await db.CreateAlertRuleAsync(a);
        }
    }

    private static string BuildListenUrl(string host, int port)
    {
        var normalizedHost = host.Trim();
        if (normalizedHost.Length == 0)
            normalizedHost = "127.0.0.1";
        if (normalizedHost.Contains(':') && !normalizedHost.StartsWith('['))
            normalizedHost = $"[{normalizedHost}]";
        return $"http://{normalizedHost}:{port}";
    }

    private static bool IsApiRequest(PathString path)
        => path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);

    private static bool IsPublicApiPath(PathString path)
    {
        var value = (path.Value ?? string.Empty).TrimEnd('/');
        if (value.Length == 0)
            value = "/";
        return _publicApiPaths.Any(p => value.Equals(p, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ExtractApiKey(HttpRequest request)
    {
        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = auth["Bearer ".Length..].Trim();
            if (token.Length > 0)
                return token;
        }
        var xApiKey = request.Headers["X-API-Key"].FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(xApiKey) ? null : xApiKey;
    }

    private static string? RequiredApiKeyScope(HttpRequest request)
    {
        var path = (request.Path.Value ?? "/").TrimEnd('/');
        if (path.Length == 0)
            path = "/";
        foreach (var (prefix, domain) in _apiKeyScopePrefixRules)
        {
            if (
                path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith($"{prefix}/", StringComparison.OrdinalIgnoreCase)
            )
            {
                var action = _readMethods.Contains(request.Method) ? "read" : "write";
                return $"{action}:{domain}";
            }
        }
        return null;
    }

    private static bool ApiKeyHasScope(IReadOnlyList<string>? scopes, string required)
    {
        var scopeSet = new HashSet<string>(
            (scopes ?? Array.Empty<string>()).Select(s => (s ?? string.Empty).Trim().ToLowerInvariant())
        );
        if (scopeSet.Contains("*"))
            return true;

        var parts = required.Split(':', 2);
        if (parts.Length != 2)
            return false;
        var action = parts[0];
        var domain = parts[1];

        if (scopeSet.Contains(required.ToLowerInvariant()) || scopeSet.Contains($"{action}:*"))
            return true;
        if (action == "read" && (scopeSet.Contains($"write:{domain}") || scopeSet.Contains("write:*")))
            return true;
        return false;
    }

    private static bool SecureEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static async Task Reject(HttpContext context, int statusCode, string detail)
    {
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new { detail });
    }

    // -----------------------------------------------------------------------
    // System tray
    // -----------------------------------------------------------------------

    private static NotifyIcon BuildTrayIcon(string dashboardUrl, Action onQuit)
    {
        var icon = new NotifyIcon
        {
            Text = "DriveChill \u2014 Fan Controller",
            Icon = GenerateSnowflakeIcon(),
        };

        var menu = new ContextMenuStrip();

        var header = new ToolStripMenuItem("DriveChill") { Enabled = false };
        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());

        var openItem = new ToolStripMenuItem("Open Dashboard");
        openItem.Font = new Font(openItem.Font, FontStyle.Bold);
        openItem.Click += (_, _) =>
            Process.Start(new ProcessStartInfo(dashboardUrl) { UseShellExecute = true });
        menu.Items.Add(openItem);

        menu.Items.Add(new ToolStripSeparator());

        var quitItem = new ToolStripMenuItem("Quit");
        quitItem.Click += (_, _) => onQuit();
        menu.Items.Add(quitItem);

        icon.ContextMenuStrip = menu;
        icon.DoubleClick += (_, _) =>
            Process.Start(new ProcessStartInfo(dashboardUrl) { UseShellExecute = true });

        return icon;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>
    /// Draws a 6-arm snowflake icon with GDI+ — no external asset files needed.
    /// Mirrors the Python Pillow implementation in tray.py.
    /// </summary>
    private static Icon GenerateSnowflakeIcon()
    {
        const int size = 64;
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.FromArgb(18, 20, 30));

        float cx = size / 2f, cy = size / 2f;
        float arm = size / 2f - size / 8f;
        float branch = arm / 3f;
        using var thick = new Pen(Color.FromArgb(120, 200, 255), Math.Max(1, size / 32));
        using var thin  = new Pen(Color.FromArgb(120, 200, 255), Math.Max(1, size / 64));

        for (int i = 0; i < 6; i++)
        {
            double angle = i * Math.PI / 3.0;
            float ex = cx + arm * (float)Math.Cos(angle);
            float ey = cy + arm * (float)Math.Sin(angle);
            g.DrawLine(thick, cx, cy, ex, ey);

            foreach (float frac in (float[])[0.35f, 0.65f])
            {
                float bx = cx + arm * frac * (float)Math.Cos(angle);
                float by = cy + arm * frac * (float)Math.Sin(angle);
                foreach (int side in (int[])[+1, -1])
                {
                    double ba = angle + side * Math.PI / 3.0;
                    g.DrawLine(thin, bx, by,
                        bx + branch * (float)Math.Cos(ba),
                        by + branch * (float)Math.Sin(ba));
                }
            }
        }

        float r = Math.Max(2, size / 20f);
        using var center = new SolidBrush(Color.FromArgb(200, 230, 255));
        g.FillEllipse(center, cx - r, cy - r, r * 2, r * 2);

        // GetHicon() allocates an unmanaged HICON — we must own and destroy it.
        var hIcon = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(hIcon).Clone(); // Clone copies to managed memory
        DestroyIcon(hIcon);
        return icon;
    }
}
