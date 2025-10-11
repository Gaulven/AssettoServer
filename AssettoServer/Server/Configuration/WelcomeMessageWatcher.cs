using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AssettoServer.Server.Configuration;
using Microsoft.Extensions.Hosting;
using Polly;
using Serilog;

namespace AssettoServer.Server.Configuration;

public class WelcomeMessageWatcher : IHostedService, IDisposable
{
    private readonly ACServerConfiguration _configuration;
    private readonly CSPServerExtraOptions _cspServerExtraOptions;
    private readonly FileSystemWatcher? _watcher;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private readonly Timer? _debounceTimer;
    private readonly string _welcomeMessagePath;
    private bool _disposed = false;

    public WelcomeMessageWatcher(ACServerConfiguration configuration, CSPServerExtraOptions cspServerExtraOptions)
    {
        _configuration = configuration;
        _cspServerExtraOptions = cspServerExtraOptions;
        
        // Determine the welcome message file path (relative to working directory)
        // When no preset is set, WelcomeMessagePath is relative to working directory
        // When preset is set, WelcomeMessagePath is relative to the preset folder
        _welcomeMessagePath = string.IsNullOrEmpty(_configuration.Server.WelcomeMessagePath) 
            ? "" 
            : _configuration.BaseFolder.StartsWith("presets") 
                ? Path.GetFullPath(Path.Join(_configuration.BaseFolder, _configuration.Server.WelcomeMessagePath))
                : Path.GetFullPath(_configuration.Server.WelcomeMessagePath);
        
        Log.Debug("WelcomeMessageWatcher: WorkingDirectory={WorkingDirectory}, WelcomeMessagePath={WelcomeMessagePath}, FinalPath={FinalPath}", 
            Directory.GetCurrentDirectory(), _configuration.Server.WelcomeMessagePath, _welcomeMessagePath);
        
        // Always initialize the debounce timer
        _debounceTimer = new Timer(OnDebounceTimer, null, Timeout.Infinite, Timeout.Infinite);
        
        if (string.IsNullOrEmpty(_welcomeMessagePath))
        {
            Log.Debug("No welcome message path configured, skipping welcome message watcher");
            return;
        }
        
        var directory = Path.GetDirectoryName(_welcomeMessagePath);
        var fileName = Path.GetFileName(_welcomeMessagePath);
        
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
        {
            Log.Warning("Invalid welcome message path: {Path}", _welcomeMessagePath);
            return;
        }
        
        // Create file system watcher for welcome message file
        try
        {
            _watcher = new FileSystemWatcher(directory, fileName);
            _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size;
            _watcher.Changed += OnFileChanged;
            _watcher.Error += OnError;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to create file system watcher for welcome message file: {Path}", _welcomeMessagePath);
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
            Log.Information("Welcome message file watcher started for {Path}", _welcomeMessagePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start welcome message file watcher");
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
            Log.Information("Welcome message file watcher stopped");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error stopping welcome message file watcher");
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
            
            var newWelcomeMessage = await ExtractWelcomeMessageAsync();
            if (newWelcomeMessage != null && newWelcomeMessage != _configuration.WelcomeMessage)
            {
                // Update the configuration's welcome message
                _configuration.UpdateWelcomeMessage(newWelcomeMessage);
                // Refresh the CSP server extra options to use the new welcome message
                _cspServerExtraOptions.RefreshWelcomeMessage();
                Log.Information("Welcome message reloaded from {Path}", _welcomeMessagePath);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to reload welcome message, keeping current value");
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    private async Task<string?> ExtractWelcomeMessageAsync()
    {
        if (!File.Exists(_welcomeMessagePath))
        {
            Log.Debug("Welcome message file not found: {Path}", _welcomeMessagePath);
            return null;
        }

        try
        {
            // Retry logic for file access (handles file locking)
            var retryPolicy = Policy
                .Handle<IOException>()
                .WaitAndRetryAsync(3, retryAttempt => 
                    TimeSpan.FromMilliseconds(100 * retryAttempt));

            return await retryPolicy.ExecuteAsync(() =>
            {
                var content = File.ReadAllText(_welcomeMessagePath);
                
                // Basic validation
                if (content.Length > 10000) // Reasonable limit for welcome message
                {
                    Log.Warning("Welcome message too long ({Length} chars), truncating", content.Length);
                    return Task.FromResult(content.Substring(0, 10000));
                }
                
                return Task.FromResult(content);
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read welcome message file: {Path}", _welcomeMessagePath);
            return null;
        }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        Log.Error(e.GetException(), "FileSystemWatcher error for welcome message file: {Path}", _welcomeMessagePath);
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
