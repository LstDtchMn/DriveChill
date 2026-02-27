using DriveChill.Hardware;
using DriveChill.Services;
using System.Diagnostics;

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
    private const string AppUrl = "http://127.0.0.1:8085";

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // Build ASP.NET Core host
        var host = BuildHost(args);
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
            try { Process.Start(new ProcessStartInfo(AppUrl) { UseShellExecute = true }); }
            catch { }
        });

        // Build tray icon — runs on main thread, blocks until Quit
        using var trayIcon = BuildTrayIcon(() =>
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

    private static IHost BuildHost(string[] args)
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
        builder.WebHost.UseUrls(AppUrl);

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
        var settings = new AppSettings();
        builder.Services.AddSingleton(settings);

        // Hardware backend: set env var DRIVECHILL_BACKEND=mock for dev/testing
        builder.Services.AddSingleton<IHardwareBackend>(_ =>
            Environment.GetEnvironmentVariable("DRIVECHILL_BACKEND") == "mock"
                ? (IHardwareBackend)new MockBackend()
                : new LhmBackend());

        // Core services
        builder.Services.AddSingleton<SensorService>();
        builder.Services.AddSingleton<FanService>();
        builder.Services.AddSingleton<AlertService>();
        builder.Services.AddSingleton<DbService>();
        builder.Services.AddSingleton<SettingsStore>();
        builder.Services.AddSingleton<ApiKeyService>();
        builder.Services.AddSingleton<WebhookService>();
        builder.Services.AddSingleton<FanTestService>();
        builder.Services.AddSingleton<WebSocketHub>();
        builder.Services
            .AddHttpClient("webhooks")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
            });

        // Background worker: polls hardware, broadcasts WebSocket messages
        builder.Services.AddHostedService<SensorWorker>();

        // CORS for Next.js dev server
        builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
            p.WithOrigins("http://localhost:3000", "http://localhost:8085")
             .AllowAnyMethod().AllowAnyHeader()));

        var app = builder.Build();

        app.UseCors();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        // Serve Next.js static export from wwwroot/
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapControllers();

        // WebSocket endpoint
        app.Map("/api/ws", async context =>
        {
            if (context.WebSockets.IsWebSocketRequest)
                await context.RequestServices.GetRequiredService<WebSocketHub>().HandleAsync(context);
            else
                context.Response.StatusCode = 400;
        });

        return app;
    }

    // -----------------------------------------------------------------------
    // System tray
    // -----------------------------------------------------------------------

    private static NotifyIcon BuildTrayIcon(Action onQuit)
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
            Process.Start(new ProcessStartInfo(AppUrl) { UseShellExecute = true });
        menu.Items.Add(openItem);

        menu.Items.Add(new ToolStripSeparator());

        var quitItem = new ToolStripMenuItem("Quit");
        quitItem.Click += (_, _) => onQuit();
        menu.Items.Add(quitItem);

        icon.ContextMenuStrip = menu;
        icon.DoubleClick += (_, _) =>
            Process.Start(new ProcessStartInfo(AppUrl) { UseShellExecute = true });

        return icon;
    }

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

        return Icon.FromHandle(bmp.GetHicon());
    }
}
