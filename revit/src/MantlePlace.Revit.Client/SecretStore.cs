using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Client;

/// <summary>
/// Per-user encrypted storage for the refresh token (<c>HPS-14</c> … <c>HPS-17</c>).
/// </summary>
/// <remarks>
/// Only the REFRESH token is ever offered to this. ⛔<c>HPS-15</c>: the access token is memory-only
/// — never written, never logged. It is short-lived and re-minted from the refresh token, so there
/// is nothing to gain by storing it and a bearer JWT to lose.
/// </remarks>
public interface ISecretStore
{
    /// <summary>
    /// Whether a saved secret will survive the process. <c>false</c> means the UI should say
    /// "you will need to sign in again next session" rather than implying persistence it does not
    /// have.
    /// </summary>
    bool IsPersistent { get; }

    /// <summary>Persists a secret. <c>false</c> when this platform cannot do so safely.</summary>
    bool Save(string key, string secret);

    /// <summary>
    /// Reads a secret. <c>false</c> covers absence AND a failed decrypt, which are the same thing to
    /// the curator (<c>HPS-17</c>): a blob written by a different OS user, or a corrupted one, means
    /// "no stored session" and not "an error happened".
    /// </summary>
    bool TryLoad(string key, out string secret);

    /// <summary>Removes a secret. Sign-out clears the store.</summary>
    void Clear(string key);
}

/// <summary>
/// <c>HPS-16</c>: the store for a platform with no secure keystore. It fails honestly.
/// </summary>
/// <remarks>
/// It does <b>not</b> fall back to writing the secret somewhere less safe. That is a downgrade
/// wearing a fallback's clothes: the plugin would look like it persisted a session and would in fact
/// have left a bearer credential in a readable file. Memory-only auth is the correct degradation.
/// </remarks>
public sealed class NullSecretStore : ISecretStore
{
    public bool IsPersistent => false;

    public bool Save(string key, string secret) => false;

    public bool TryLoad(string key, out string secret)
    {
        secret = string.Empty;
        return false;
    }

    public void Clear(string key)
    {
    }
}

/// <summary>Picks the store this platform can honestly provide.</summary>
public static class SecretStores
{
    /// <summary>DPAPI on Windows; the null store everywhere else.</summary>
    public static ISecretStore ForCurrentPlatform()
        => OperatingSystem.IsWindows() ? new DpapiSecretStore() : new NullSecretStore();
}

/// <summary>
/// ⛔<c>HPS-14</c> on Windows: DPAPI, scoped to the logged-in OS user.
/// </summary>
/// <remarks>
/// <para>
/// <c>CryptProtectData</c> with <c>CRYPTPROTECT_UI_FORBIDDEN</c> and <b>without</b>
/// <c>CRYPTPROTECT_LOCAL_MACHINE</c>, so the key derives from the user rather than the machine —
/// on a shared workstation, machine scope would let every account decrypt every other account's
/// refresh token.
/// </para>
/// <para>
/// P/Invoked rather than taken from <c>System.Security.Cryptography.ProtectedData</c> because that
/// type ships in a NuGet package and this tree takes none (<c>revit/Directory.Build.props</c>). The
/// package is a thin wrapper over exactly these two entry points.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    /// <summary>Never prompt. This can run during Revit's add-in load, where a modal dialog hangs.</summary>
    private const int CryptprotectUiForbidden = 0x1;

    private readonly string _root;

    public DpapiSecretStore()
        : this(DefaultRoot())
    {
    }

    /// <summary>As the default constructor, with an explicit directory. For tests.</summary>
    public DpapiSecretStore(string root) => _root = root;

    public bool IsPersistent => true;

    public bool Save(string key, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        if (!TryProtect(Encoding.UTF8.GetBytes(secret), out byte[] encrypted))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(_root);
            File.WriteAllBytes(PathFor(key), encrypted);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool TryLoad(string key, out string secret)
    {
        secret = string.Empty;

        byte[] encrypted;
        try
        {
            string path = PathFor(key);
            if (!File.Exists(path))
            {
                return false;
            }

            encrypted = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (!TryUnprotect(encrypted, out byte[] plain))
        {
            // HPS-17. Indistinguishable from absence to the curator, and treated as such.
            return false;
        }

        secret = Encoding.UTF8.GetString(plain);
        Array.Clear(plain);
        return true;
    }

    public void Clear(string key)
    {
        try
        {
            string path = PathFor(key);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sign-out has already dropped the in-memory tokens. A file that would not delete is
            // worth no dialog: the blob is useless without this plugin, and the next Save
            // overwrites it.
        }
    }

    private static string DefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MantlePlace",
        "auth");

    /// <summary>
    /// A storage key becomes a file name only after ⛔<c>HPS-30</c> sanitisation, the same mapping
    /// the bundle cache uses.
    /// </summary>
    private string PathFor(string key)
        => Path.Combine(_root, CacheKeySanitiser.Sanitise(key).DirectoryName + ".bin");

    private static bool TryProtect(byte[] plain, out byte[] encrypted)
    {
        encrypted = [];

        DataBlob output = default;
        GCHandle pinned = GCHandle.Alloc(plain, GCHandleType.Pinned);
        try
        {
            DataBlob input = new() { Size = plain.Length, Data = pinned.AddrOfPinnedObject() };
            if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out output))
            {
                return false;
            }

            encrypted = Copy(output);
            return true;
        }
        finally
        {
            pinned.Free();
            Release(ref output);
        }
    }

    private static bool TryUnprotect(byte[] encrypted, out byte[] plain)
    {
        plain = [];

        DataBlob output = default;
        GCHandle pinned = GCHandle.Alloc(encrypted, GCHandleType.Pinned);
        try
        {
            DataBlob input = new() { Size = encrypted.Length, Data = pinned.AddrOfPinnedObject() };
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out output))
            {
                return false;
            }

            plain = Copy(output);
            return true;
        }
        finally
        {
            pinned.Free();
            Release(ref output);
        }
    }

    private static byte[] Copy(DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero || blob.Size <= 0)
        {
            return [];
        }

        byte[] bytes = new byte[blob.Size];
        Marshal.Copy(blob.Data, bytes, 0, blob.Size);
        return bytes;
    }

    /// <summary>
    /// Frees a blob crypt32 allocated. Skipping this leaks the decrypted refresh token into the
    /// process heap for the life of the Revit session.
    /// </summary>
    private static void Release(ref DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero)
        {
            return;
        }

        if (blob.Size > 0)
        {
            // Zero it before handing the pages back: LocalFree does not scrub.
            for (int offset = 0; offset < blob.Size; offset++)
            {
                Marshal.WriteByte(blob.Data, offset, 0);
            }
        }

        _ = LocalFree(blob.Data);
        blob = default;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    // DllImport rather than the source-generated LibraryImport: that generator emits `unsafe`
    // marshalling code and so requires AllowUnsafeBlocks across the whole assembly. Three blittable
    // signatures do not justify granting unsafe to the assembly that handles the refresh token.
    // ExactSpelling because crypt32 exports these names with no W/A suffix to probe for.
    [DllImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr descriptionOut,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr handle);
}
