using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using MantlePlace.Core;

namespace MantlePlace.Client;

public interface ISecretStore
{
    const string LockName = "MantlePlace.Auth.SecretStore";

    bool IsPersistent { get; }

    bool Save(string key, string secret);

    bool TryLoad(string key, out string secret);

    void Clear(string key);
}

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

public static class SecretStores
{
    public static ISecretStore ForCurrentPlatform()
        => OperatingSystem.IsWindows() ? new DpapiSecretStore() : new NullSecretStore();
}

[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    private const int CryptprotectUiForbidden = 0x1;

    private readonly string _root;

    public DpapiSecretStore()
        : this(DefaultRoot())
    {
    }

    public DpapiSecretStore(string root) => _root = root;

    private static Mutex? AcquireLock()
    {
        Mutex mutex = new(initiallyOwned: false, ISecretStore.LockName);
        try
        {
            mutex.WaitOne(TimeSpan.FromSeconds(5));
        }
        catch (AbandonedMutexException)
        {
        }

        return mutex;
    }

    private static void ReleaseLock(Mutex? mutex)
    {
        if (mutex is null)
        {
            return;
        }

        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        mutex.Dispose();
    }

    public bool IsPersistent => true;

    public bool Save(string key, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        if (!TryProtect(Encoding.UTF8.GetBytes(secret), out byte[] encrypted))
        {
            return false;
        }

        Mutex? storeLock = AcquireLock();
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
        finally
        {
            ReleaseLock(storeLock);
        }
    }

    public bool TryLoad(string key, out string secret)
    {
        secret = string.Empty;

        byte[] encrypted;
        Mutex? storeLock = AcquireLock();
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
        finally
        {
            ReleaseLock(storeLock);
        }

        if (!TryUnprotect(encrypted, out byte[] plain))
        {
            return false;
        }

        secret = Encoding.UTF8.GetString(plain);
        Array.Clear(plain);
        return true;
    }

    public void Clear(string key)
    {
        Mutex? storeLock = AcquireLock();
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
        }
        finally
        {
            ReleaseLock(storeLock);
        }
    }

    private static string DefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MantlePlace",
        "auth");

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

    private static void Release(ref DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero)
        {
            return;
        }

        if (blob.Size > 0)
        {
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
