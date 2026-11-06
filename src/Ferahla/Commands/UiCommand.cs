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

    public static async Task<int> Run(bool noBrowser, int port, CancellationToken ct)
    {
        var home = SnapshotStore.DefaultHome();
        if (InstanceInfo.Load(home) is { } running && await running.AliveAsync())
        {
            Console.WriteLine(Lang.T("Ferahla is already open; showing it.", "Ferahla zaten açık; o gösteriliyor."));
            if (!noBrowser) Open(running.LaunchUrl);
            else Console.WriteLine(running.LaunchUrl);
            return ExitCodes.Ok;
        }

        await using var server = await FerahlaServer.StartAsync(new ServerOptions { Home = home, Port = port, IdleTimeout = noBrowser ? null : TimeSpan.FromMinutes(3) });
        new InstanceInfo { Pid = Environment.ProcessId, Port = server.Port, Token = server.Token }.Save(home);
        try
        {
            Console.WriteLine(Lang.T(
                $"Ferahla is running at {server.BaseUrl}. It closes by itself a few minutes after the last tab; Ctrl+C stops it now.",
                $"Ferahla {server.BaseUrl} adresinde çalışıyor. Son sekme kapandıktan birkaç dakika sonra kendiliğinden kapanır; hemen durdurmak için Ctrl+C."));
            if (noBrowser) Console.WriteLine(server.LaunchUrl);
            else Open(server.LaunchUrl);
            await server.WaitForShutdownAsync(ct);
        }
        finally
        {
            if (InstanceInfo.Load(home) is { } mine && mine.Pid == Environment.ProcessId)
                try { File.Delete(InstanceInfo.PathIn(home)); } catch (IOException) { }
        }
        return ExitCodes.Ok;
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
