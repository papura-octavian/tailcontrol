using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using TailControl.Shared.Contracts;

namespace TailControl.LockHelper;

/// <summary>
/// Ruleaza in sesiunea utilizatorului (pornit la logare). Singurul lucru pe care il face e
/// <c>LockWorkStation</c> cand serviciul trimite <c>LOCK</c>; orice alt mesaj e ignorat.
/// Daca serviciul nu ruleaza, reincearca periodic conexiunea.
/// </summary>
internal static partial class Program
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private static async Task<int> Main()
    {
        // O singura instanta per sesiune.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\TailControl.LockHelper", out var createdNew);
        if (!createdNew)
        {
            return 0;
        }

        while (true)
        {
            try
            {
                await ServeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
            {
                // Serviciul nu ruleaza sau s-a oprit; reincercam.
            }

            await Task.Delay(RetryDelay).ConfigureAwait(false);
        }
    }

    private static async Task ServeAsync()
    {
        using var pipe = new NamedPipeClientStream(".", LockPipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync((int)RetryDelay.TotalMilliseconds).ConfigureAwait(false);

        using var reader = new StreamReader(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (!string.Equals(line, LockPipeProtocol.LockRequest, StringComparison.Ordinal))
            {
                continue;
            }

            var reply = LockWorkStation()
                ? LockPipeProtocol.Ok
                : $"{LockPipeProtocol.ErrorPrefix} {Marshal.GetLastPInvokeError()}";
            await writer.WriteLineAsync(reply).ConfigureAwait(false);
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LockWorkStation();
}
