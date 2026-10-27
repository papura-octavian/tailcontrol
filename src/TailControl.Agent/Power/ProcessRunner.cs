using System.Diagnostics;

namespace TailControl.Agent.Power;

public interface IProcessRunner
{
    /// <summary>Porneste procesul si intoarce codul de iesire.</summary>
    Task<int> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken);
}

/// <summary>
/// Singurul loc din agent care apeleaza <see cref="Process.Start(ProcessStartInfo)"/>.
/// Refuza orice configuratie care ar putea interpreta text: shell, cale relativa,
/// argumente sub forma de string concatenat.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public async Task<int> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        EnsureSafe(startInfo);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Procesul {startInfo.FileName} nu a pornit.");

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);

        return process.ExitCode;
    }

    internal static void EnsureSafe(ProcessStartInfo startInfo)
    {
        if (startInfo.UseShellExecute)
        {
            throw new InvalidOperationException("UseShellExecute nu este permis.");
        }

        if (!Path.IsPathFullyQualified(startInfo.FileName))
        {
            throw new InvalidOperationException("Executabilul trebuie dat cu cale absoluta.");
        }

        if (!string.IsNullOrEmpty(startInfo.Arguments))
        {
            throw new InvalidOperationException("Argumentele se dau doar prin ArgumentList, nu ca string.");
        }

        if (!string.Equals(startInfo.FileName, SystemCommands.ShutdownExePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Executabil nepermis: {startInfo.FileName}.");
        }
    }
}
