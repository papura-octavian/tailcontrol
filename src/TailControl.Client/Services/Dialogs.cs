namespace TailControl.Client.Services;

public enum UserVerificationResult
{
    Verified,
    Denied,

    /// <summary>Dispozitivul nu are amprenta/PIN/Windows Hello configurat.</summary>
    NotAvailable,
}

/// <summary>Confirmarea identitatii cu mecanismul dispozitivului (amprenta, fata, PIN, Windows Hello).</summary>
public interface IUserVerifier
{
    Task<UserVerificationResult> VerifyAsync(string reason);
}

public sealed class UnsupportedUserVerifier : IUserVerifier
{
    public Task<UserVerificationResult> VerifyAsync(string reason) => Task.FromResult(UserVerificationResult.NotAvailable);
}

public interface IDialogs
{
    Task<bool> ConfirmAsync(string title, string message, string accept, string cancel = "Renunta");

    Task AlertAsync(string title, string message);

    Task ToastAsync(string message);
}

public sealed class ShellDialogs : IDialogs
{
    public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel = "Renunta") =>
        Page.DisplayAlertAsync(title, message, accept, cancel);

    public Task AlertAsync(string title, string message) =>
        Page.DisplayAlertAsync(title, message, "OK");

    public Task ToastAsync(string message) =>
        Page.DisplayAlertAsync("TailControl", message, "OK");

    private static Page Page => Shell.Current ?? throw new InvalidOperationException("Shell-ul nu este inca afisat.");
}
