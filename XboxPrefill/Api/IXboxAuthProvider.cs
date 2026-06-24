#nullable enable

namespace XboxPrefill.Api;

/// <summary>
/// Interface for surfacing the Xbox device-code login challenge to the user.
/// Xbox uses the OAuth2 device-code flow: the daemon shows a short <c>user_code</c> + a verification URL,
/// the user enters the code in their browser, and the daemon polls for the resulting tokens internally.
/// </summary>
public interface IXboxAuthProvider
{
    /// <summary>
    /// Presents the device-code challenge to the user (prints / broadcasts the code + verification URL).
    /// The daemon then polls the token endpoint itself, so this does not return a credential.
    /// </summary>
    /// <param name="userCode">The short code the user types at the verification URL.</param>
    /// <param name="verificationUri">The URL the user opens, e.g. https://www.microsoft.com/link.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PresentDeviceCodeAsync(string userCode, string verificationUri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels any pending login challenge.
    /// </summary>
    void CancelPendingRequest();
}
