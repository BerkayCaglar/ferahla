using System.CommandLine;
using System.Diagnostics;
using Ferahla.Core.Cli;
using Ferahla.Core.Storage;
using Ferahla.Core.Text;
using Ferahla.Server;

namespace Ferahla.Commands;

/// <summary><c>ferahla</c> with no arguments, or <c>ferahla ui</c>: the local browser UI.</summary>
internal static class UiCommand
{
    public static readonly Option<bool> NoBrowser = new("--no-browser") { Description = Lang.T("Don't open the browser; print the address", "Tarayıcıyı açma; adresi yaz") };
    public static readonly Option<int> Port = new("--port") { Description = Lang.T("Port (default: a random free one)", "Port (varsayılan: rastgele boş bir port)") };

    public static Command Create()
    {
        var cmd = new Command("ui", Lang.T("Open the Ferahla window in your browser (the default)", "Ferahla penceresini tarayıcıda aç (varsayılan)"));
        cmd.Options.Add(NoBrowser); cmd.Options.Add(Port);
        cmd.SetAction((pr, ct) => Run(pr.GetValue(NoBrowser), pr.GetValue(Port), ct));
        return cmd;
    }

    // Set only by a Ferahla that just updated itself, for the new exe it starts (same address, so the open tab reloads).
    private const string RestartPort = "FERAHLA_RESTART_PORT", RestartToken = "FERAHLA_RESTART_TOKEN";

    public static async Task<int> Run(bool noBrowser, int port, CancellationToken ct)
    {
        var home = SnapshotStore.DefaultHome();
        var (restartPort, restartToken) = TakeRestart();
        if (restartToken is null && InstanceInfo.Load(home) is { } running && await running.AliveAsync())
        {
            Console.WriteLine(Lang.T("Ferahla is already open; showing it.", "Ferahla zaten açık; o gösteriliyor."));
            if (!noBrowser) Open(running.LaunchUrl);
            else Console.WriteLine(running.LaunchUrl);
            return ExitCodes.Ok;
        }
        _ = FerahlaServer.CleanupAfterUpdate(home);
        if (restartToken is not null) { port = restartPort; await WaitForPort(port, ct); }

        bool restart;
        await using (var server = await FerahlaServer.StartAsync(new ServerOptions
        {
            Home = home, Port = port, Token = restartToken, CheckUpdates = true,
            IdleTimeout = noBrowser ? null : TimeSpan.FromMinutes(3),
        }))
        {
            new InstanceInfo { Pid = Environment.ProcessId, Port = server.Port, Token = server.Token }.Save(home);
            try
            {
                Console.WriteLine(Lang.T(
                    $"Ferahla is running at {server.BaseUrl}. It closes by itself a few minutes after the last tab; Ctrl+C stops it now.",
                    $"Ferahla {server.BaseUrl} adresinde çalışıyor. Son sekme kapandıktan birkaç dakika sonra kendiliğinden kapanır; hemen durdurmak için Ctrl+C."));
                if (noBrowser) Console.WriteLine(server.LaunchUrl);
                else if (restartToken is null) Open(server.LaunchUrl);
                await server.WaitForShutdownAsync(ct);
            }
            finally
            {
                if (InstanceInfo.Load(home) is { } mine && mine.Pid == Environment.ProcessId)
                    try { File.Delete(InstanceInfo.PathIn(home)); } catch (IOException) { }
            }
            restart = server.RestartRequested;
            port = server.Port;
            restartToken = server.Token;
        }
        // The server is disposed (port released): start the new exe that now has this exe's name.
        if (restart && Environment.ProcessPath is { } exe)
        {
            Console.WriteLine(Lang.T("Updated; restarting…", "Güncellendi; yeniden başlatılıyor…"));
            var psi = new ProcessStartInfo(exe, noBrowser ? "ui --no-browser" : "ui") { UseShellExecute = false };
            psi.Environment[RestartPort] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            psi.Environment[RestartToken] = restartToken;
            Process.Start(psi)?.Dispose();
        }
        return ExitCodes.Ok;
    }

    /// <summary>Reads and clears the restart variables, so they never reach child processes (Claude, uninstallers).</summary>
    private static (int Port, string? Token) TakeRestart()
    {
        var p = Environment.GetEnvironmentVariable(RestartPort);
        var t = Environment.GetEnvironmentVariable(RestartToken);
        Environment.SetEnvironmentVariable(RestartPort, null);
        Environment.SetEnvironmentVariable(RestartToken, null);
        if (t is null || !System.Text.RegularExpressions.Regex.IsMatch(t, "^[0-9a-f]{64}$")) return (0, null);
        if (!int.TryParse(p, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535) return (0, null);
        return (port, t);
    }

    /// <summary>The old process may still hold the port for a moment.</summary>
    private static async Task WaitForPort(int port, CancellationToken ct)
    {
        for (var i = 0; i < 40; i++)
        {
            try
            {
                var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                l.Start(); l.Stop();
                return;
            }
            catch (System.Net.Sockets.SocketException) { await Task.Delay(250, ct); }
        }
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.WriteLine(Lang.T($"Open this address in your browser: {url}", $"Bu adresi tarayıcında aç: {url}"));
        }
    }
}
