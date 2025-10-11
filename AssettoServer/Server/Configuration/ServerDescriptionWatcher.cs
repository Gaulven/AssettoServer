using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AssettoServer.Server.Configuration;
using Microsoft.Extensions.Hosting;
using Polly;
using Serilog;

namespace AssettoServer.Server.Configuration;

public class ServerDescriptionWatcher : IHostedService, IDisposable
{
    private readonly ACServerConfiguration _configuration;
    private readonly FileSystemWatcher? _watcher;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private readonly Timer? _debounceTimer;
    private readonly string _serverDescriptionPath;
    private bool _disposed = false;

    public ServerDescriptionWatcher(ACServerConfiguration configuration)
    {
        _configuration = configuration;
        
        // Determine the server description file path (relative to working directory)
        _serverDescriptionPath = string.IsNullOrEmpty(_configuration.Extra.ServerDescriptionPath) 
            ? "" 
            : Path.GetFullPath(_configuration.Extra.ServerDescriptionPath);
        
        Log.Debug("ServerDescriptionWatcher: WorkingDirectory={WorkingDirectory}, ServerDescriptionPath={ServerDescriptionPath}, FinalPath={FinalPath}", 
            Directory.GetCurrentDirectory(), _configuration.Extra.ServerDescriptionPath, _serverDescriptionPath);
        
        // Always initialize the debounce timer
        _debounceTimer = new Timer(OnDebounceTimer, null, Timeout.Infinite, Timeout.Infinite);
        
        if (string.IsNullOrEmpty(_serverDescriptionPath))
        {
            Log.Debug("No server description path configured, skipping server description watcher");
            return;
        }
        
        var directory = Path.GetDirectoryName(_serverDescriptionPath);
        var fileName = Path.GetFileName(_serverDescriptionPath);
        
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
        {
            Log.Warning("Invalid server description path: {Path}", _serverDescriptionPath);
            return;
        }
        
        // Create file system watcher for server description file
        try
        {
            _watcher = new FileSystemWatcher(directory, fileName);
            _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size;
            _watcher.Changed += OnFileChanged;
            _watcher.Error += OnError;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to create file system watcher for server description file: {Path}", _serverDescriptionPath);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_watcher == null)
        {
            return Task.CompletedTask;
        }
        
        try
        {
            _watcher.EnableRaisingEvents = true;
            Log.Information("Server description file watcher started for {Path}", _serverDescriptionPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start server description file watcher");
        }
        
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_watcher == null)
        {
            return Task.CompletedTask;
        }
        
        try
        {
            _watcher.EnableRaisingEvents = false;
            Log.Information("Server description file watcher stopped");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error stopping server description file watcher");
        }
        
        return Task.CompletedTask;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (e.ChangeType == WatcherChangeTypes.Changed && !_disposed && _debounceTimer != null)
        {
            // Debounce rapid changes (common on Windows)
            _debounceTimer.Change(500, Timeout.Infinite);
        }
    }

    private async void OnDebounceTimer(object? state)
    {
        if (_disposed) return;
        
        await _reloadLock.WaitAsync();
        try
        {
            // Small delay to ensure file is fully written
            await Task.Delay(100);
            
            var newDescription = await ExtractServerDescriptionAsync();
            if (newDescription != null && newDescription != _configuration.ServerDescription)
            {
                // Update the configuration's server description
                _configuration.UpdateServerDescription(newDescription);
                Log.Information("Server description reloaded from {Path}", _serverDescriptionPath);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to reload server description, keeping current value");
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    private async Task<string?> ExtractServerDescriptionAsync()
    {
        if (!File.Exists(_serverDescriptionPath))
        {
            Log.Debug("Server description file not found: {Path}", _serverDescriptionPath);
            return null;
        }

        try
        {
            // Retry logic for file access (handles file locking)
            var retryPolicy = Policy
                .Handle<IOException>()
                .WaitAndRetryAsync(3, retryAttempt => 
                    TimeSpan.FromMilliseconds(100 * retryAttempt));

            return await retryPolicy.ExecuteAsync(async () =>
            {
                var content = await File.ReadAllTextAsync(_serverDescriptionPath);
                return content;
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read server description file: {Path}", _serverDescriptionPath);
            return null;
        }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        Log.Error(e.GetException(), "FileSystemWatcher error for server description file: {Path}", _serverDescriptionPath);
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        _debounceTimer?.Dispose();
        _watcher?.Dispose();
        _reloadLock?.Dispose();
    }
}
