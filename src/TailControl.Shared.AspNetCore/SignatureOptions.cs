namespace TailControl.Shared.AspNetCore;

public sealed class SignatureOptions
{
    /// <summary>Calea fisierului cu clienti autorizati (scriibil doar de administrator).</summary>
    public string AuthorizedClientsPath { get; set; } = "";

    /// <summary>Dimensiunea maxima a corpului unei cereri semnate.</summary>
    public int MaxBodyBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// Respinge cererile semnate inainte de pornirea serverului. Cache-ul de nonce
    /// e in memorie si se pierde la repornire; fara asta, o cerere interceptata ar
    /// putea fi retrimisa imediat dupa o repornire.
    /// </summary>
    public bool RejectSignedBeforeStartup { get; set; } = true;

    public int FailuresBeforeBlock { get; set; } = 5;

    public TimeSpan FailureWindow { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan BlockDuration { get; set; } = TimeSpan.FromMinutes(5);
}
