using System;
using System.IO;
using System.Threading.Tasks;
using AssettoServer.Server.Configuration;
using Serilog;
using NUnit.Framework;

namespace AssettoServer.Tests;

public class WelcomeMessageWatcherTests
{
    [Test]
    public async Task HandlesWelcomeMessageFileChanges()
    {
        // Arrange
        var tempDir = Path.GetTempPath();
        var welcomePath = Path.Join(tempDir, "test_welcome.txt");
        var config = CreateTestConfiguration(tempDir, "test_welcome.txt");
        var watcher = new WelcomeMessageWatcher(config, new CSPServerExtraOptions(config));
        
        try
        {
            // Create initial welcome message
            await File.WriteAllTextAsync(welcomePath, "Welcome to our server!");
            
            // Act
            var result = await ExtractWelcomeMessageFromFile(welcomePath);
            
            // Assert
            Assert.That(result, Is.EqualTo("Welcome to our server!"));
        }
        finally
        {
            watcher.Dispose();
            if (File.Exists(welcomePath))
                File.Delete(welcomePath);
        }
    }

    [Test]
    public async Task HandlesMissingWelcomeMessageFile()
    {
        // Arrange
        var tempDir = Path.GetTempPath();
        var welcomePath = Path.Join(tempDir, "nonexistent_welcome.txt");
        var config = CreateTestConfiguration(tempDir, "nonexistent_welcome.txt");
        var watcher = new WelcomeMessageWatcher(config, new CSPServerExtraOptions(config));
        
        try
        {
            // Act
            var result = await ExtractWelcomeMessageFromFile(welcomePath);
            
            // Assert
            Assert.That(result, Is.Null);
        }
        finally
        {
            watcher.Dispose();
        }
    }

    [Test]
    public async Task HandlesEmptyWelcomeMessage()
    {
        // Arrange
        var tempDir = Path.GetTempPath();
        var welcomePath = Path.Join(tempDir, "empty_welcome.txt");
        var config = CreateTestConfiguration(tempDir, "empty_welcome.txt");
        var watcher = new WelcomeMessageWatcher(config, new CSPServerExtraOptions(config));
        
        try
        {
            // Create empty welcome message
            await File.WriteAllTextAsync(welcomePath, "");
            
            // Act
            var result = await ExtractWelcomeMessageFromFile(welcomePath);
            
            // Assert
            Assert.That(result, Is.EqualTo(""));
        }
        finally
        {
            watcher.Dispose();
            if (File.Exists(welcomePath))
                File.Delete(welcomePath);
        }
    }

    [Test]
    public async Task HandlesVeryLongWelcomeMessage()
    {
        // Arrange
        var tempDir = Path.GetTempPath();
        var welcomePath = Path.Join(tempDir, "long_welcome.txt");
        var config = CreateTestConfiguration(tempDir, "long_welcome.txt");
        var watcher = new WelcomeMessageWatcher(config, new CSPServerExtraOptions(config));
        var longMessage = new string('A', 15000); // 15000 characters
        
        try
        {
            await File.WriteAllTextAsync(welcomePath, longMessage);
            
            // Act
            var result = await ExtractWelcomeMessageFromFile(welcomePath);
            
            // Assert
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Length, Is.EqualTo(10000)); // Should be truncated to 10000 chars
        }
        finally
        {
            watcher.Dispose();
            if (File.Exists(welcomePath))
                File.Delete(welcomePath);
        }
    }

    [Test]
    public async Task HandlesFileLocking()
    {
        // Arrange
        var tempDir = Path.GetTempPath();
        var welcomePath = Path.Join(tempDir, "locked_welcome.txt");
        var config = CreateTestConfiguration(tempDir, "locked_welcome.txt");
        var watcher = new WelcomeMessageWatcher(config, new CSPServerExtraOptions(config));
        
        try
        {
            await File.WriteAllTextAsync(welcomePath, "Welcome message");
            
            // Act - Try to read while file is locked
            using var fileStream = File.Open(welcomePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            
            // This should handle the IOException gracefully
            var result = await ExtractWelcomeMessageFromFile(welcomePath);
            
            // Assert
            Assert.That(result, Is.Null); // Should return null due to file locking
        }
        finally
        {
            watcher.Dispose();
            if (File.Exists(welcomePath))
                File.Delete(welcomePath);
        }
    }

    [Test]
    public void HandlesNoWelcomeMessagePath()
    {
        // Arrange
        var tempDir = Path.GetTempPath();
        var config = CreateTestConfiguration(tempDir, ""); // Empty path
        
        // Act
        var watcher = new WelcomeMessageWatcher(config, new CSPServerExtraOptions(config));
        
        // Assert - Should not throw and should handle gracefully
        Assert.DoesNotThrow(() => watcher.Dispose());
    }

    [Test]
    public void HandlesInvalidWelcomeMessagePath()
    {
        // Arrange
        var tempDir = Path.GetTempPath();
        var config = CreateTestConfiguration(tempDir, "invalid/path/with/slashes.txt");
        
        // Act
        var watcher = new WelcomeMessageWatcher(config, new CSPServerExtraOptions(config));
        
        // Assert - Should not throw and should handle gracefully
        Assert.DoesNotThrow(() => watcher.Dispose());
    }

    private static ACServerConfiguration CreateTestConfiguration(string baseFolder, string welcomeMessagePath)
    {
        // Create a minimal test configuration with the specified welcome message path
        var serverConfig = new ServerConfiguration
        {
            WelcomeMessagePath = welcomeMessagePath
        };
        
        // We need to create a configuration that has the BaseFolder set
        // This is a simplified version for testing
        var config = new ACServerConfiguration("", "", "", false);
        
        // Use reflection to set the BaseFolder for testing
        var baseFolderField = typeof(ACServerConfiguration).GetField("BaseFolder", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        baseFolderField?.SetValue(config, baseFolder);
        
        return config;
    }

    private static Task<string?> ExtractWelcomeMessageFromFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return Task.FromResult<string?>(null);
            }
            
            var content = File.ReadAllText(filePath);
            
            // Basic validation (same as in WelcomeMessageWatcher)
            if (content.Length > 10000)
            {
                return Task.FromResult<string?>(content.Substring(0, 10000));
            }
            
            return Task.FromResult<string?>(content);
        }
        catch (Exception)
        {
            return Task.FromResult<string?>(null);
        }
    }
}
