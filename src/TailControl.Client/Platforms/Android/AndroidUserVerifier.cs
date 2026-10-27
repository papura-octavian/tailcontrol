using Android.Hardware.Biometrics;
using Android.OS;
using Java.Lang;
using AuthenticatorTypes = Android.Hardware.Biometrics.BiometricManagerAuthenticators;

namespace TailControl.Client.Services;

/// <summary>
/// BiometricPrompt din sistem (fara pachete AndroidX): amprenta/fata sau PIN-ul/modelul de
/// deblocare al telefonului. Combinatia BIOMETRIC_STRONG | DEVICE_CREDENTIAL cere Android 11+.
/// </summary>
public sealed class AndroidUserVerifier : IUserVerifier
{
    private const AuthenticatorTypes Authenticators = AuthenticatorTypes.BiometricStrong | AuthenticatorTypes.DeviceCredential;

    public Task<UserVerificationResult> VerifyAsync(string reason)
    {
        var activity = Platform.CurrentActivity;
        if (activity is null
            || activity.GetSystemService(Android.Content.Context.BiometricService) is not BiometricManager manager
            || manager.CanAuthenticate((int)Authenticators) != (int)BiometricCode.Success)
        {
            return Task.FromResult(UserVerificationResult.NotAvailable);
        }

        var result = new TaskCompletionSource<UserVerificationResult>();
        activity.RunOnUiThread(() =>
        {
            var prompt = new BiometricPrompt.Builder(activity)
                .SetTitle("Confirma comanda")
                .SetSubtitle(reason)
                .SetAllowedAuthenticators((int)Authenticators)
                .Build();
            prompt.Authenticate(new CancellationSignal(), activity.MainExecutor!, new Callback(result));
        });

        return result.Task;
    }

    private sealed class Callback(TaskCompletionSource<UserVerificationResult> result) : BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult? authResult) =>
            result.TrySetResult(UserVerificationResult.Verified);

        // Anulare sau prea multe incercari; o singura incercare gresita nu inchide dialogul.
        public override void OnAuthenticationError(BiometricErrorCode errorCode, ICharSequence? errString) =>
            result.TrySetResult(UserVerificationResult.Denied);
    }
}
