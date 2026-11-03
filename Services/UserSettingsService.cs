using System.IO;
using System.Text.Json;

namespace WalletCopy.Services;

public sealed class UserSettingsService
{
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WalletCopy",
        "settings.json");

    public async Task<UserSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
        {
            return new UserSettings();
        }

        await using var stream = File.OpenRead(_settingsPath);
        return await JsonSerializer.DeserializeAsync<UserSettings>(stream, cancellationToken: cancellationToken)
               ?? new UserSettings();
    }

    public async Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_settingsPath)
                        ?? throw new InvalidOperationException("Não foi possível determinar a pasta de configurações.");
        Directory.CreateDirectory(directory);
        await using var stream = File.Create(_settingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, cancellationToken: cancellationToken);
    }
}

public sealed class UserSettings
{
    public string OwnWalletAddress { get; set; } = "";
    public string FocusedWalletAddress { get; set; } = "";
    public bool HasCompletedInitialSetup { get; set; }
    public List<string> TrackedWalletAddresses { get; set; } = [];
}
