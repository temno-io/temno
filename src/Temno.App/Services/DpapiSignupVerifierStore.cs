using Temno.Core.Account;
using Temno.Core.IO;

namespace Temno.App.Services;

/// <summary>
/// WP-390: the pending sign-up verifier under DPAPI for the current user, beside the project's session:
/// <c>account\&lt;key&gt;\signup-verifier.dpapi</c> (<see cref="ProjectSignupVerifierStore"/>).
/// </summary>
public sealed class DpapiSignupVerifierStore : ISignupVerifierStore
{
    private readonly ProjectSignupVerifierStore _store;

    /// <param name="projectUrl">The current Supabase URL, or null when sign-in is not configured.</param>
    public DpapiSignupVerifierStore(Func<string?> projectUrl)
    {
        _store = new ProjectSignupVerifierStore(
            Path.Combine(UserDataRoots.LocalAppDataRoot, "account"),
            projectUrl,
            new DpapiAccountStore.Dpapi());
    }

    public PendingSignupVerifier? Load() => _store.Load();

    public void Save(PendingSignupVerifier pending) => _store.Save(pending);

    public void Clear() => _store.Clear();
}
