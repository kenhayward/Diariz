using Microsoft.Win32;

namespace Diariz.OutlookReader;

/// <summary>Answers "is classic Outlook actually installed on this PC?" by reading the registry only - it never
/// creates the COM object.
///
/// <para>That distinction is the entire point. <c>Outlook.Application</c> is registered by Office as a whole,
/// so it is present on a PC that has Word and Excel but no Outlook, and on one migrated to the new Outlook.
/// Activating it there does not simply fail: COM hands the request to Windows Installer, which pops up an
/// <i>install Outlook</i> dialog in front of whatever the user was doing. Diariz ran a sync on every launch, so
/// those users got the prompt every launch.</para>
///
/// <para>The rule is therefore "find a real <c>OUTLOOK.EXE</c> on disk, or assume there is none". Both the
/// class registration (<c>LocalServer32</c>) and the <c>App Paths</c> entry are consulted, and each is read in
/// both registry views - a 32-bit Office on 64-bit Windows registers under <c>Wow6432Node</c>, and this process
/// is 64-bit. A false "no" is recoverable: Preferences has a re-check, and the stored answer is cleared by
/// it.</para></summary>
public static class OutlookPresence
{
    /// <summary>Where Windows records the executable behind a COM class, and where Office records Outlook's own
    /// path. An advertised-but-not-installed component has the first and not the second, and its
    /// <c>LocalServer32</c> path does not exist on disk - which is what this looks for.</summary>
    private const string ProgId = "Outlook.Application";
    private const string AppPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\OUTLOOK.EXE";

    /// <summary>The result of a probe: the executable when one was found, else the reason there was not.</summary>
    public sealed record Presence(bool Installed, string? Reason, string? ExecutablePath);

    public static Presence Detect()
    {
        if (!OperatingSystem.IsWindows())
            return new Presence(false, "not-windows", null);

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            if (FromClassRegistration(view) is { } fromClass) return new Presence(true, null, fromClass);
            if (FromAppPaths(view) is { } fromPaths) return new Presence(true, null, fromPaths);
        }

        return new Presence(false, "not-installed", null);
    }

    /// <summary><c>Outlook.Application</c> -> its CLSID -> the local server executable, if that file is really
    /// there. Returns null at the first step that does not lead to an existing file, which covers both a
    /// missing ProgID and an advertised class whose server was never installed.</summary>
    private static string? FromClassRegistration(RegistryView view)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
            using var clsidKey = root.OpenSubKey($@"{ProgId}\CLSID");
            if (clsidKey?.GetValue(null) is not string clsid || string.IsNullOrWhiteSpace(clsid)) return null;

            using var server = root.OpenSubKey($@"CLSID\{clsid}\LocalServer32");
            if (server is null) return null;

            // The default value is the command line, not a bare path: it is usually quoted and often carries
            // /automation. The optional "ServerExecutable" value is the bare path when Office wrote one.
            var path = server.GetValue("ServerExecutable") as string ?? server.GetValue(null) as string;
            return ExistingExecutable(path);
        }
        catch
        {
            // A locked-down or unreadable hive is not proof of anything; fall through to the other lookups.
            return null;
        }
    }

    /// <summary>The <c>App Paths</c> entry, which Office writes only when Outlook itself is installed.</summary>
    private static string? FromAppPaths(RegistryView view)
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = machine.OpenSubKey(AppPaths);
            return ExistingExecutable(key?.GetValue(null) as string);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Pull the executable out of a registered command line and return it only if it is on disk.</summary>
    internal static string? ExistingExecutable(string? command)
    {
        var path = ExecutablePath(command);
        return path is not null && File.Exists(path) ? path : null;
    }

    /// <summary>The executable part of a <c>LocalServer32</c> command line. Quoted paths win outright; an
    /// unquoted one is cut at <c>.exe</c>, because the arguments that follow (<c>/automation</c>) are not part
    /// of the path and "Program Files" means it cannot simply be split on spaces.</summary>
    internal static string? ExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var value = command.Trim();

        if (value.StartsWith('"'))
        {
            var close = value.IndexOf('"', 1);
            return close > 1 ? value[1..close] : null;
        }

        var exe = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe >= 0 ? value[..(exe + 4)] : value;
    }
}
