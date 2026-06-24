#nullable enable

namespace XboxPrefill.Api;

/// <summary>
/// Authentication provider that surfaces the Xbox device-code login challenge over the socket.
/// Broadcasts a credential-challenge event carrying the <c>user_code</c> + verification URL so the frontend
/// can display them. The daemon polls the MSA token endpoint internally, so no credential is returned here.
/// </summary>
public sealed class SocketAuthProvider : IXboxAuthProvider, IDisposable
{
    private readonly SocketServer _socketServer;
    private readonly IPrefillProgress _progress;
    private string? _currentChallengeId;
    private bool _disposed;

    public SocketAuthProvider(SocketServer socketServer, IPrefillProgress? progress = null)
    {
        _socketServer = socketServer;
        _progress = progress ?? NullProgress.Instance;
    }

    /// <summary>
    /// Kept for socket-command compatibility. The device-code flow does not require the client to send back a
    /// credential (the daemon polls), so a received credential is simply acknowledged.
    /// </summary>
    public void ReceiveCredential(EncryptedCredentialResponse response)
    {
        _progress.OnLog(LogLevel.Debug, $"Received credential for challenge {response.ChallengeId} (device-code flow does not require it)");
    }

    /// <summary>
    /// Broadcasts the device-code challenge (user_code + verification URL) to all connected clients.
    /// </summary>
    public async Task PresentDeviceCodeAsync(string userCode, string verificationUri, CancellationToken cancellationToken = default)
    {
        var challenge = SecureCredentialExchange.CreateChallenge("device-code", verificationUri, userCode);
        _currentChallengeId = challenge.ChallengeId;

        _progress.OnLog(LogLevel.Info, $"Sign in at {verificationUri} and enter code: {userCode}");

        var challengeEvent = new CredentialChallengeEvent(challenge);
        await _socketServer.BroadcastCredentialChallengeAsync(challengeEvent, cancellationToken);
    }

    /// <summary>
    /// Cancels any pending login challenge.
    /// </summary>
    public void CancelPendingRequest()
    {
        _progress.OnLog(LogLevel.Info, "Cancelling pending login challenge...");
        _currentChallengeId = null;
    }

    public string? CurrentChallengeId => _currentChallengeId;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
