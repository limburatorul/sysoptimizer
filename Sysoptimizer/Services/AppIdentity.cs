using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Sysoptimizer.Services;

public enum SignatureState { Signed, Unsigned, Untrusted }

/// <summary>
/// An exe's icon and whether it's signed, the way AppControl shows them next to each app. Both are worked
/// out once per path on a background thread (a signature check reads the whole file) and cached; callers get
/// null until it's ready and are told when it is.
/// </summary>
public static class AppIdentity
{
    public sealed record Info(ImageSource? Icon, SignatureState Signature);

    private static readonly ConcurrentDictionary<string, Info> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> Pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The cached info, or null while it's being worked out — <paramref name="ready"/> runs on the UI thread when it is.</summary>
    public static Info? Get(string path, Action<string> ready)
    {
        if (Cache.TryGetValue(path, out var info)) return info;
        if (Pending.TryAdd(path, 0))
        {
            var dispatcher = Application.Current.Dispatcher;
            Task.Run(() =>
            {
                Cache[path] = new Info(IconOf(path), Check(path));
                Pending.TryRemove(path, out _);
                dispatcher.BeginInvoke(() => ready(path));
            });
        }
        return null;
    }

    /// <summary>The signature, checked now (cached) — for the "unsigned app started" alert, off the UI thread.</summary>
    public static SignatureState SignatureOf(string path) =>
        Cache.TryGetValue(path, out var info) ? info.Signature : Check(path);

    private static ImageSource? IconOf(string path)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            image.Freeze(); // made here, shown on the UI thread
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or COMException) { return null; }
    }

    // --- Authenticode ---

    private static SignatureState Check(string path)
    {
        int result = VerifyEmbedded(path);
        if (result == 0) return SignatureState.Signed;
        // Windows' own exes carry no signature of their own: they're listed, by hash, in a signed system
        // catalog. Checking only the embedded one would call svchost "unsigned".
        if (result is TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN)
            return InSystemCatalog(path) ? SignatureState.Signed : SignatureState.Unsigned;
        return SignatureState.Untrusted; // signed, but the signature is broken, revoked or from an untrusted root
    }

    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);
    private const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private static int VerifyEmbedded(string path)
    {
        var file = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        IntPtr filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,           // WTD_UI_NONE
                fdwRevocationChecks = 0,  // WTD_REVOKE_NONE: no network round trip per app
                dwUnionChoice = 1,        // WTD_CHOICE_FILE
                pFile = filePtr,
                dwStateAction = 1,        // WTD_STATEACTION_VERIFY
                dwProvFlags = 0x1000,     // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            int result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
            data.dwStateAction = 2;       // WTD_STATEACTION_CLOSE
            WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
            return result;
        }
        finally { Marshal.FreeHGlobal(filePtr); }
    }

    /// <summary>
    /// Whether the file's hash is in one of Windows' system catalogs.
    /// ponytail: being listed is taken as signed — the catalog's own signature isn't re-verified here,
    /// since only an admin can add catalogs. Verify it with WTD_CHOICE_CATALOG if that ever matters.
    /// </summary>
    private static bool InSystemCatalog(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            foreach (var algorithm in new[] { "SHA256", "SHA1" })
            {
                if (!CryptCATAdminAcquireContext2(out IntPtr admin, IntPtr.Zero, algorithm, IntPtr.Zero, 0)) continue;
                try
                {
                    uint size = 0;
                    CryptCATAdminCalcHashFromFileHandle2(admin, stream.SafeFileHandle.DangerousGetHandle(), ref size, null, 0);
                    if (size == 0) continue;
                    var hash = new byte[size];
                    stream.Position = 0;
                    if (!CryptCATAdminCalcHashFromFileHandle2(admin, stream.SafeFileHandle.DangerousGetHandle(), ref size, hash, 0)) continue;
                    IntPtr catalog = CryptCATAdminEnumCatalogFromHash(admin, hash, size, 0, IntPtr.Zero);
                    if (catalog == IntPtr.Zero) continue;
                    CryptCATAdminReleaseCatalogContext(admin, catalog, 0);
                    return true;
                }
                finally { CryptCATAdminReleaseContext(admin, 0); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WINTRUST_DATA data);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr admin, IntPtr subsystem, string hashAlgorithm, IntPtr strongHashPolicy, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr admin, IntPtr file, ref uint hashSize, byte[]? hash, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr admin, byte[] hash, uint hashSize, uint flags, IntPtr previous);

    [DllImport("wintrust.dll")]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr admin, IntPtr catalog, uint flags);

    [DllImport("wintrust.dll")]
    private static extern bool CryptCATAdminReleaseContext(IntPtr admin, uint flags);
}
