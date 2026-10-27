namespace TailControl.Shared.Contracts;

/// <summary>
/// Protocolul dintre serviciul agentului (Session 0) si helper-ul de blocare din sesiunea
/// utilizatorului. Serviciul e serverul de named pipe; helper-ul se conecteaza la logare.
/// Mesajele sunt linii UTF-8. Serviciul trimite doar <see cref="LockRequest"/>, iar helper-ul
/// raspunde cu <see cref="Ok"/> sau <c>ERR cod</c>. Helper-ul ignora orice alt mesaj.
/// </summary>
public static class LockPipeProtocol
{
    public const string PipeName = "TailControl.Agent.Lock";

    public const string LockRequest = "LOCK";

    public const string Ok = "OK";

    public const string ErrorPrefix = "ERR";
}
