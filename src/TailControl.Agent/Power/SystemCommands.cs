using System.Diagnostics;
using System.Globalization;

namespace TailControl.Agent.Power;

/// <summary>
/// Singurele procese externe pe care agentul le poate porni. Calea e absoluta, iar
/// argumentele sunt constante: clientul nu poate influenta nimic din ele. Intarzierea
/// vine din configurarea locala si e validata.
/// </summary>
public static class SystemCommands
{
    public const string ShutdownComment = "TailControl: oprire ceruta de la distanta";

    /// <summary>Codul intors de shutdown.exe cand exista deja o oprire programata.</summary>
    public const int ErrorShutdownIsScheduled = 1190;

    /// <summary>Codul intors de <c>shutdown /a</c> cand nu exista nicio oprire de anulat.</summary>
    public const int ErrorNoShutdownInProgress = 1116;

    public static string ShutdownExePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");

    /// <summary>
    /// <c>shutdown /s /t N /c "..." /d p:0:0</c>. Cu <c>/t</c> mai mare ca 0, Windows inchide
    /// fortat aplicatiile deschise (<c>/f</c> implicit).
    /// </summary>
    public static ProcessStartInfo ScheduleShutdown(TimeSpan delay)
    {
        var seconds = (int)delay.TotalSeconds;
        if (seconds is < 5 or > 600)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), "Intarzierea trebuie sa fie intre 5 si 600 de secunde.");
        }

        var psi = Base();
        psi.ArgumentList.Add("/s");
        psi.ArgumentList.Add("/t");
        psi.ArgumentList.Add(seconds.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(ShutdownComment);
        psi.ArgumentList.Add("/d");
        psi.ArgumentList.Add("p:0:0");
        return psi;
    }

    /// <summary><c>shutdown /a</c>: anuleaza oprirea programata.</summary>
    public static ProcessStartInfo AbortShutdown()
    {
        var psi = Base();
        psi.ArgumentList.Add("/a");
        return psi;
    }

    private static ProcessStartInfo Base() => new ProcessStartInfo(ShutdownExePath)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
}
