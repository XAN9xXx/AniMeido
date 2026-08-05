using AniMeido.Plugin.AI.Models;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AniMeido.Plugin.AI.Services;

internal sealed class DpapiSecretStore
{
    private const int CryptprotectUiForbidden = 0x1;
    private readonly AiPluginPaths _paths;

    public DpapiSecretStore(AiPluginPaths paths)
        => _paths = paths;

    public string? LoadApiKey(
        AiProviderKind provider,
        bool includeLegacyKey = true)
    {
        var keys = LoadKeys(out var legacyKey);
        return keys.TryGetValue(provider.ToString(), out var apiKey)
            ? apiKey
            : includeLegacyKey ? legacyKey : null;
    }

    public void SaveApiKey(AiProviderKind provider, string? apiKey)
    {
        var keys = LoadKeys(out _);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            keys.Remove(provider.ToString());
            if (keys.Count == 0)
            {
                DeleteSecrets();
                return;
            }
        }
        else
        {
            keys[provider.ToString()] = apiKey.Trim();
        }

        SaveKeys(keys);
    }

    private Dictionary<string, string> LoadKeys(out string? legacyKey)
    {
        legacyKey = null;
        if (!File.Exists(_paths.SecretsPath))
        {
            return new(StringComparer.Ordinal);
        }

        var encrypted = File.ReadAllBytes(_paths.SecretsPath);
        var clear = Unprotect(encrypted);
        try
        {
            var text = Encoding.UTF8.GetString(clear);
            try
            {
                var envelope = JsonSerializer.Deserialize<SecretEnvelope>(text);
                return envelope?.ApiKeys is null
                    ? new(StringComparer.Ordinal)
                    : new(envelope.ApiKeys, StringComparer.Ordinal);
            }
            catch (JsonException)
            {
                legacyKey = string.IsNullOrWhiteSpace(text) ? null : text;
                return new(StringComparer.Ordinal);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private void SaveKeys(IReadOnlyDictionary<string, string> keys)
    {
        var clear = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new SecretEnvelope(1, new(keys, StringComparer.Ordinal))));
        try
        {
            var encrypted = Protect(clear);
            var tempPath = _paths.SecretsPath + ".tmp";
            File.WriteAllBytes(tempPath, encrypted);
            File.Move(tempPath, _paths.SecretsPath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private void DeleteSecrets()
    {
        if (File.Exists(_paths.SecretsPath))
        {
            File.Delete(_paths.SecretsPath);
        }
    }

    private sealed record SecretEnvelope(
        int SchemaVersion,
        Dictionary<string, string> ApiKeys);

    private static byte[] Protect(byte[] clear)
        => Transform(clear, protect: true);

    private static byte[] Unprotect(byte[] encrypted)
        => Transform(encrypted, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        try
        {
            var inputBlob = new DataBlob(
                input.Length,
                inputHandle.AddrOfPinnedObject());
            DataBlob outputBlob;
            var succeeded = protect
                ? CryptProtectData(
                    ref inputBlob,
                    null,
                    0,
                    0,
                    0,
                    CryptprotectUiForbidden,
                    out outputBlob)
                : CryptUnprotectData(
                    ref inputBlob,
                    0,
                    0,
                    0,
                    0,
                    CryptprotectUiForbidden,
                    out outputBlob);
            if (!succeeded)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var result = new byte[outputBlob.Length];
                Marshal.Copy(outputBlob.Data, result, 0, outputBlob.Length);
                return result;
            }
            finally
            {
                _ = LocalFree(outputBlob.Data);
            }
        }
        finally
        {
            inputHandle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;

        public nint Data;

        public DataBlob(int length, nint data)
        {
            Length = length;
            Data = data;
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob input,
        string? description,
        nint entropy,
        nint reserved,
        nint prompt,
        int flags,
        out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input,
        nint description,
        nint entropy,
        nint reserved,
        nint prompt,
        int flags,
        out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
