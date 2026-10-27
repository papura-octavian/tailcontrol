using Windows.Security.Credentials.UI;

namespace TailControl.Client.Services;

/// <summary>Windows Hello (PIN, amprenta sau fata), legat de fereastra aplicatiei.</summary>
public sealed class WindowsUserVerifier : IUserVerifier
{
    public async Task<UserVerificationResult> VerifyAsync(string reason)
    {
        if (await UserConsentVerifier.CheckAvailabilityAsync() != UserConsentVerifierAvailability.Available)
        {
            return UserVerificationResult.NotAvailable;
        }

        var windows = Application.Current?.Windows;
        var window = windows is { Count: > 0 } ? windows[0].Handler?.PlatformView as Microsoft.UI.Xaml.Window : null;
        if (window is null)
        {
            return UserVerificationResult.NotAvailable;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var result = await UserConsentVerifierInterop.RequestVerificationForWindowAsync(hwnd, reason);
        return result == UserConsentVerificationResult.Verified
            ? UserVerificationResult.Verified
            : UserVerificationResult.Denied;
    }
}
