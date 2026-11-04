using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WalletCopy.Models;

namespace WalletCopy.Services;

public sealed class TradingCredentialService
{
    private readonly string _credentialsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WalletCopy",
        "credentials.dat");

    public async Task<TradingCredentials?> LoadCredentialsAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_credentialsPath))
        {
            return null;
        }

        try
        {
            var encryptedBytes = await File.ReadAllBytesAsync(_credentialsPath, cancellationToken);
            var decryptedBytes = ProtectedData.Unprotect(
                encryptedBytes,
                null,
                DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(decryptedBytes);
            return JsonSerializer.Deserialize<TradingCredentials>(json);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveCredentialsAsync(TradingCredentials credentials, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_credentialsPath)
                        ?? throw new InvalidOperationException("Não foi possível determinar o diretório de dados.");
        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(credentials);
        var plainBytes = Encoding.UTF8.GetBytes(json);
        var encryptedBytes = ProtectedData.Protect(
            plainBytes,
            null,
            DataProtectionScope.CurrentUser);

        await File.WriteAllBytesAsync(_credentialsPath, encryptedBytes, cancellationToken);
    }

    public void ClearCredentials()
    {
        if (File.Exists(_credentialsPath))
        {
            File.Delete(_credentialsPath);
        }
    }
}

