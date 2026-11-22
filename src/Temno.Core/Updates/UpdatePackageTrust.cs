using System.Runtime.InteropServices;

namespace Temno.Core.Updates;

public enum UpdateTrustVerdict
{
    Trusted,
    Unsigned,
    Tampered,
    Untrusted,
    Unavailable,
}

/// <summary>Windows' verdict on a package, with the HRESULT behind it.</summary>
public sealed record UpdateTrustResult(UpdateTrustVerdict Verdict, int Code)
{
    public string CodeText => "0x" + Code.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Asks Windows whether a package's signature is good: <c>WinVerifyTrust</c> through the AppX SIP, the
/// check <c>signtool verify /pa</c> runs. It hashes the contents, follows the chain to a trusted root, and
/// honours the timestamp, so a signature outlives Artifact Signing's three-day certificate. Revocation is left
/// to the Windows installer, which checks it again. Off Windows there is no verdict, and no update installs.
/// </summary>
public static class UpdatePackageTrust
{
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckNone = 0x10;
    private const uint WtdDisableMd2Md4 = 0x2000;

    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private const int TrustEBadDigest = unchecked((int)0x80096010);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static UpdateTrustResult Verify(string packagePath)
    {
        if (!OperatingSystem.IsWindows())
            return new UpdateTrustResult(UpdateTrustVerdict.Unavailable, 0);

        var path = Marshal.StringToHGlobalUni(Path.GetFullPath(packagePath));
        var file = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(
                new WintrustFileInfo
                {
                    CbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
                    PcwszFilePath = path,
                },
                file,
                fDeleteOld: false);
            var data = new WintrustData
            {
                CbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                DwUIChoice = WtdUiNone,
                FdwRevocationChecks = WtdRevokeNone,
                DwUnionChoice = WtdChoiceFile,
                PFile = file,
                DwStateAction = WtdStateActionVerify,
                DwProvFlags = WtdRevocationCheckNone | WtdDisableMd2Md4,
            };
            var action = GenericVerifyV2;
            var code = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            data.DwStateAction = WtdStateActionClose;
            _ = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return new UpdateTrustResult(Classify(code), code);
        }
        finally
        {
            Marshal.FreeHGlobal(file);
            Marshal.FreeHGlobal(path);
        }
    }

    internal static UpdateTrustVerdict Classify(int code) => code switch
    {
        0 => UpdateTrustVerdict.Trusted,
        TrustENoSignature or TrustESubjectFormUnknown => UpdateTrustVerdict.Unsigned,
        TrustEBadDigest => UpdateTrustVerdict.Tampered,
        _ => UpdateTrustVerdict.Untrusted,
    };

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionId, ref WintrustData pWvtData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint CbStruct;
        public IntPtr PcwszFilePath;
        public IntPtr HFile;
        public IntPtr PgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint CbStruct;
        public IntPtr PPolicyCallbackData;
        public IntPtr PSipClientData;
        public uint DwUIChoice;
        public uint FdwRevocationChecks;
        public uint DwUnionChoice;
        public IntPtr PFile;
        public uint DwStateAction;
        public IntPtr HWvtStateData;
        public IntPtr PwszUrlReference;
        public uint DwProvFlags;
        public uint DwUIContext;
        public IntPtr PSignatureSettings;
    }
}
