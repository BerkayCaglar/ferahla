using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Ferahla.Core.Cli;
using Ferahla.Core.Json;

namespace Ferahla.Commands;

internal static class Output
{
    public static void Json<T>(T value, JsonTypeInfo<T> info)
    {
        Console.Out.Write(JsonSerializer.Serialize(value, info));
        Console.Out.WriteLine();
    }

    public static int Error(bool json, string message, string? detail = null, int code = ExitCodes.Error)
    {
        if (json) Json(new ErrorResult { Error = message, Detail = detail }, FerahlaJson.Indented.ErrorResult);
        else
        {
            Console.Error.WriteLine(message);
            if (detail is not null) Console.Error.WriteLine(detail);
        }
        return code;
    }

    /// <summary>Progress line on stderr, only when stderr is an interactive console.</summary>
    public static void Status(string text)
    {
        if (Console.IsErrorRedirected) return;
        var width = Math.Max(20, SafeWidth() - 1);
        if (text.Length > width) text = text[..(width - 1)] + "…";
        Console.Error.Write("\r" + text.PadRight(width));
    }

    public static void EndStatus()
    {
        if (Console.IsErrorRedirected) return;
        Console.Error.Write("\r" + new string(' ', Math.Max(20, SafeWidth() - 1)) + "\r");
    }

    private static int SafeWidth()
    {
        try { return Console.WindowWidth; } catch { return 80; }
    }
}
