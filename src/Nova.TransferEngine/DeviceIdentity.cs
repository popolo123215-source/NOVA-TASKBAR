using System.Security.Cryptography;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Nova.Core.Contracts;

namespace Nova.TransferEngine;

public sealed class DeviceIdentity : IDisposable
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NOVA.TaskBar.DeviceIdentity.v1");
    private readonly bool _ownsCertificate;

    private DeviceIdentity(string deviceId, string displayName, X509Certificate2 certificate)
    {
        Device = new DeviceRef(deviceId, displayName, "windows");
        Certificate = certificate;
        Fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        _ownsCertificate = true;
    }

    public DeviceRef Device { get; }
    public X509Certificate2 Certificate { get; }
    public string Fingerprint { get; }

    public static DeviceIdentity LoadOrCreate(string stateDirectory, string? displayName = null)
    {
        Directory.CreateDirectory(stateDirectory);
        var idPath = Path.Combine(stateDirectory, "device-id.txt");
        var pfxPath = Path.Combine(stateDirectory, "device-identity.dpapi");
        var deviceId = File.Exists(idPath) ? File.ReadAllText(idPath).Trim() : Guid.NewGuid().ToString("N");
        if (!Guid.TryParseExact(deviceId, "N", out _))
        {
            deviceId = Guid.NewGuid().ToString("N");
            File.WriteAllText(idPath, deviceId);
        }
        else if (!File.Exists(idPath))
        {
            File.WriteAllText(idPath, deviceId);
        }

        X509Certificate2 certificate;
        if (File.Exists(pfxPath))
        {
            var protectedPfx = File.ReadAllBytes(pfxPath);
            var pfx = Unprotect(protectedPfx, Entropy);
            try { certificate = X509CertificateLoader.LoadPkcs12(pfx, (string?)null, X509KeyStorageFlags.UserKeySet); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
        else
        {
            using var rsa = RSA.Create(3072);
            var request = new CertificateRequest($"CN=NOVA TaskBar {deviceId}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
            var rawPfx = created.Export(X509ContentType.Pfx);
            try
            {
                File.WriteAllBytes(pfxPath, Protect(rawPfx, Entropy));
                certificate = X509CertificateLoader.LoadPkcs12(rawPfx, (string?)null, X509KeyStorageFlags.UserKeySet);
            }
            finally { CryptographicOperations.ZeroMemory(rawPfx); }
        }

        return new DeviceIdentity(deviceId, displayName ?? Environment.MachineName, certificate);
    }

    private static byte[] Protect(byte[] data, byte[] entropy) => TransformWithDpapi(data, entropy, true);
    private static byte[] Unprotect(byte[] data, byte[] entropy) => TransformWithDpapi(data, entropy, false);

    private static byte[] TransformWithDpapi(byte[] data, byte[] entropy, bool protect)
    {
        var input = CreateBlob(data);
        var optionalEntropy = CreateBlob(entropy);
        try
        {
            DataBlob output;
            var succeeded = protect
                ? CryptProtectData(ref input, "NOVA TaskBar device identity", ref optionalEntropy, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref optionalEntropy, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!succeeded) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally
        {
            FreeBlob(ref input);
            FreeBlob(ref optionalEntropy);
        }
    }

    private static DataBlob CreateBlob(byte[] bytes)
    {
        var blob = new DataBlob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
        return blob;
    }

    private static void FreeBlob(ref DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero) return;
        Marshal.Copy(new byte[blob.Size], 0, blob.Data, blob.Size);
        Marshal.FreeHGlobal(blob.Data);
        blob = default;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string description, ref DataBlob optionalEntropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob optionalEntropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
    public static string? DeviceIdFromCertificate(X509Certificate certificate)
    {
        using var parsedCertificate = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        var commonName = parsedCertificate.GetNameInfo(X509NameType.SimpleName, false);
        const string prefix = "NOVA TaskBar ";
        if (!commonName.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var candidate = commonName[prefix.Length..];
        return Guid.TryParseExact(candidate, "N", out _) ? candidate : null;
    }

    public static DeviceIdentity CreateEphemeral(string displayName)
    {
        using var rsa = RSA.Create(2048);
        var id = Guid.NewGuid().ToString("N");
        var request = new CertificateRequest($"CN=NOVA TaskBar {id}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2));
        return new DeviceIdentity(id, displayName, X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.UserKeySet));
    }

    public void Dispose()
    {
        if (_ownsCertificate) Certificate.Dispose();
    }

    private sealed record IdentityFile(string DeviceId);
}
