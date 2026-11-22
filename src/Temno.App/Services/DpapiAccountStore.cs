using System.Security.Cryptography;
using Temno.Core.Account;
using Temno.Core.IO;

namespace Temno.App.Services;

/// <summary>
/// The signed-in session under DPAPI for the current user, one store per Supabase project:
/// <c>account\&lt;key&gt;\session.dpapi</c> (<see cref="AccountStoreKey"/>).
/// </summary>
public sealed class DpapiAccountStore : IAccountTokenStore
{
    private readonly ProjectSessionStore _store;

    /// <param name="projectUrl">The current Supabase URL, or null when sign-in is not configured.</param>
    /// <param name="accountRoot">The <c>account</c> directory; defaults to the user's LocalAppData.</param>
    public DpapiAccountStore(Func<string?> projectUrl, string? accountRoot = null)
    {
        _store = new ProjectSessionStore(
            accountRoot ?? Path.Combine(UserDataRoots.LocalAppDataRoot, "account"),
            projectUrl,
            new Dpapi());
    }

    /// <summary>Writes nothing when the current project did not issue the token (<see cref="ProjectSessionStore.TrySave"/>).</summary>
    public void Save(AccountTokenBundle bundle)
    {
        if (!_store.TrySave(bundle))
            System.Diagnostics.Debug.WriteLine("Temno account: session not stored; the current project did not issue its token.");
    }

    public AccountTokenBundle? Load() => _store.Load();

    public void Clear() => _store.Clear();

    /// <summary>The current user's DPAPI; also protects the pending sign-up verifier (<see cref="DpapiSignupVerifierStore"/>).</summary>
    internal sealed class Dpapi : ISessionProtector
    {
        public byte[] Protect(byte[] plain, byte[] entropy)
            => ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser);

        public byte[] Unprotect(byte[] protectedBytes, byte[] entropy)
            => ProtectedData.Unprotect(protectedBytes, entropy, DataProtectionScope.CurrentUser);
    }
}
