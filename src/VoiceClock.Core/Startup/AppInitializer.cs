using VoiceClock.Core.Diagnostics;
using VoiceClock.Core.Settings;
using VoiceClock.Core.Storage;

namespace VoiceClock.Core.Startup;

public sealed record InitializationReport(SettingsLoadResult Settings, BundledVoiceInfo BundledVoice);

/// <summary>
/// Loads settings and recordings exactly once, whichever entry point needs them first
/// (the main screen, the setup screens, or the home-screen widget). A failed attempt can be retried.
/// </summary>
public sealed class AppInitializer
{
    private readonly ISettingsRepository _settings;
    private readonly IVoiceAssetRepository _repository;
    private readonly IAppLog _log;
    private readonly object _sync = new();
    private Task<InitializationReport>? _initialization;

    public AppInitializer(ISettingsRepository settings, IVoiceAssetRepository repository, IAppLog? log = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _log = log ?? NullAppLog.Instance;
    }

    public Task<InitializationReport> EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        Task<InitializationReport> task;
        lock (_sync)
        {
            if (_initialization is null || _initialization.IsFaulted || _initialization.IsCanceled)
            {
                _initialization = Task.Run(InitializeAsync, CancellationToken.None);
            }

            task = _initialization;
        }

        return task.WaitAsync(cancellationToken);
    }

    private async Task<InitializationReport> InitializeAsync()
    {
        var settings = await _settings.LoadAsync().ConfigureAwait(false);
        if (settings.Outcome == SettingsLoadOutcome.RecoveredFromCorruption)
        {
            _log.Warn("Settings were damaged and have been reset to defaults.");
        }

        await _repository.InitializeAsync().ConfigureAwait(false);
        return new InitializationReport(settings, _repository.BundledVoice);
    }
}
