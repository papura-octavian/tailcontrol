using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using TailControl.Agent.Power;
using TailControl.Shared.Contracts;

namespace TailControl.Agent.Locking;

public enum LockOutcome
{
    Locked,
    NoSession,
    Failed,
}

public interface ILockBroker
{
    /// <summary>Numarul de sesiuni de utilizator in care ruleaza helper-ul de blocare.</summary>
    int ConnectedSessions { get; }

    /// <summary>Cere blocarea tuturor sesiunilor in care ruleaza helper-ul.</summary>
    Task<LockOutcome> LockAllSessionsAsync(CancellationToken cancellationToken);
}

public sealed class LockBrokerOptions
{
    public string PipeName { get; set; } = LockPipeProtocol.PipeName;

    public int MaxConnections { get; set; } = 8;

    public TimeSpan ReplyTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Doar pentru teste: runner-ele de CI pot rula in Session 0.</summary>
    internal bool AllowSessionZeroForTests { get; set; }
}

/// <summary>
/// Un serviciu din Session 0 nu poate bloca sesiunea interactiva. Serviciul ofera un named pipe
/// la care se conecteaza helper-ul pornit la logare in fiecare sesiune de utilizator, iar
/// blocarea e delegata helper-ului (<c>LockWorkStation</c>).
/// <para>
/// ACL-ul pipe-ului permite doar SYSTEM, administratorilor, contului serviciului si utilizatorilor
/// interactivi. Accesul din retea e interzis explicit. Conexiunile din Session 0 sunt refuzate.
/// </para>
/// </summary>
public sealed partial class LockBroker(LockBrokerOptions options, ILogger<LockBroker> logger)
    : BackgroundService, ILockBroker
{
    private readonly ConcurrentDictionary<HelperConnection, byte> _connections = new();

    public int ConnectedSessions => _connections.Keys.Select(c => c.SessionId).Distinct().Count();

    public async Task<LockOutcome> LockAllSessionsAsync(CancellationToken cancellationToken)
    {
        var connections = _connections.Keys.ToList();
        if (connections.Count == 0)
        {
            return LockOutcome.NoSession;
        }

        var results = await Task.WhenAll(connections.Select(c => c.SendLockAsync(options.ReplyTimeout, cancellationToken))).ConfigureAwait(false);
        if (results.Any(r => r == true))
        {
            return LockOutcome.Locked;
        }

        // null = conexiunea s-a inchis intre timp (utilizatorul s-a delogat).
        return results.All(r => r is null) ? LockOutcome.NoSession : LockOutcome.Failed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_connections.Count >= options.MaxConnections)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                continue;
            }

            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe(options.PipeName);
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);

                if (!NativeMethods.GetNamedPipeClientSessionId(pipe.SafePipeHandle.DangerousGetHandle(), out var sessionId)
                    || (sessionId == 0 && !options.AllowSessionZeroForTests))
                {
                    LogRejectedClient(logger, sessionId);
                    pipe.Dispose();
                    continue;
                }

                var connection = new HelperConnection(pipe, (int)sessionId);
                pipe = null;
                _connections.TryAdd(connection, 0);
                LogConnected(logger, connection.SessionId);
                _ = connection.RunReaderAsync(stoppingToken).ContinueWith(
                    _ =>
                    {
                        _connections.TryRemove(connection, out var _);
                        connection.Dispose();
                        LogDisconnected(logger, connection.SessionId);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException ex)
            {
                LogPipeError(logger, ex);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                pipe?.Dispose();
            }
        }

        foreach (var connection in _connections.Keys)
        {
            connection.Dispose();
        }
    }

    internal static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var self = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    private static NamedPipeServerStream CreatePipe(string name) =>
        NamedPipeServerStreamAcl.Create(
            name,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            CreatePipeSecurity());

    [LoggerMessage(Level = LogLevel.Information, Message = "Helper de blocare conectat (sesiunea {SessionId})")]
    private static partial void LogConnected(ILogger logger, int sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Helper de blocare deconectat (sesiunea {SessionId})")]
    private static partial void LogDisconnected(ILogger logger, int sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conexiune refuzata pe pipe-ul de blocare (sesiunea {SessionId})")]
    private static partial void LogRejectedClient(ILogger logger, uint sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Eroare pe pipe-ul de blocare")]
    private static partial void LogPipeError(ILogger logger, Exception exception);

    private sealed class HelperConnection(NamedPipeServerStream pipe, int sessionId) : IDisposable
    {
        private readonly StreamReader _reader = new(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        private readonly StreamWriter _writer = new(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private TaskCompletionSource<string?>? _pendingReply;
        private volatile bool _closed;

        public int SessionId { get; } = sessionId;

        /// <summary>Citeste raspunsurile helper-ului; se termina cand helper-ul se deconecteaza.</summary>
        public async Task RunReaderAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    Volatile.Read(ref _pendingReply)?.TrySetResult(line);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
            finally
            {
                _closed = true;
                Volatile.Read(ref _pendingReply)?.TrySetResult(null);
            }
        }

        /// <summary><c>true</c> = blocat, <c>false</c> = eroare raportata, <c>null</c> = conexiune inchisa.</summary>
        public async Task<bool?> SendLockAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_closed)
                {
                    return null;
                }

                var reply = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _pendingReply, reply);
                await _writer.WriteLineAsync(LockPipeProtocol.LockRequest.AsMemory(), cancellationToken).ConfigureAwait(false);
                var line = await reply.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
                return line is null ? null : line == LockPipeProtocol.Ok;
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return null;
            }
            finally
            {
                Volatile.Write(ref _pendingReply, null);
                _sendLock.Release();
            }
        }

        public void Dispose()
        {
            _closed = true;
            _reader.Dispose();
            _writer.Dispose();
            pipe.Dispose();
        }
    }
}
