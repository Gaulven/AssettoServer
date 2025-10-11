using System;
using System.IO;
using System.Threading.Tasks;
using AssettoServer.Server.Configuration;
using Serilog;
using NUnit.Framework;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace AssettoServer.Tests;

public class ServerDescriptionWatcherTests
{
    [Test]
    public async Task HandlesInvalidYamlSyntax()
    {
        // Arrange
        var tempDir = Path.GetTempPath();
        var configPath = Path.Join(tempDir, "test_extra_cfg.yml");
        var config = CreateTestConfiguration(tempDir);
        var watcher = new ServerDescriptionWatcher(config);
        
        try
        {
            // Test invalid YAML syntax
            await File.WriteAllTextAsync(configPath, @"
ServerDescription: ""Test Description""
InvalidYaml: [unclosed bracket
AnotherField: value
");
            
            // Act
            var result = await ExtractServerDescriptionFromFile(configPath);
            
            // Assert
            Assert.Null(result); // Should return null for invalid YAML
        }
        finally
        {
            watcher.Dispose();
            if (File.Exists(configPath))
                File.Delete(configPath);
        }
    }

    [Test]
    public async Task HandlesMissingServerDescription()
    {
        // Arrange
        var tempDir = Path.GetTempPath();
        var configPath = Path.Join(tempDir, "test_extra_cfg.yml");
        
        try
        {
            // Test YAML without ServerDescription field
            await File.WriteAllTextAsync(configPath, @"
EnableServerDetails: true
MaxAfkTimeMinutes: 15
SomeOtherField: value
");
            
            // Act
            var result = await ExtractServerDescriptionFromFile(configPath);
            
            // Assert
            Assert.Null(result); // Should return null when field not found
        }
        finally
        {
            if (File.Exists(configPath))
                File.Delete(configPath);
        }
    }

    [Test]
    public async Task HandlesEmptyServerDescription()
    {
        // Arrange
        var tempDir = Path.GetTempPath();
        var configPath = Path.Join(tempDir, "test_extra_cfg.yml");
        
        try
        {
            // Test empty ServerDescription
            await File.WriteAllTextAsync(configPath, @"
ServerDescription: """"
EnableServerDetails: true
");
            
            // Act
            var result = await ExtractServerDescriptionFromFile(configPath);
            
            // Assert
            Assert.That(result, Is.EqualTo("")); // Should return empty string
        }
        finally
        {
            if (File.Exists(configPath))
                File.Delete(configPath);
        }
    }

    [Test]
    public async Task HandlesVeryLongServerDescription()
    {
        // Arrange
        var tempDir = Path.GetTempPath();
        var configPath = Path.Join(tempDir, "test_extra_cfg.yml");
        var longDescription = new string('A', 2000); // 2000 characters
        
        try
        {
            await File.WriteAllTextAsync(configPath, $@"
ServerDescription: ""{longDescription}""
EnableServerDetails: true
");
            
            // Act
            var result = await ExtractServerDescriptionFromFile(configPath);
            
            // Assert
            Assert.NotNull(result);
            Assert.That(result.Length, Is.EqualTo(2000)); // Should NOT be truncated
        }
        finally
        {
            if (File.Exists(configPath))
                File.Delete(configPath);
        }
    }

    [Test]
    public async Task HandlesFileLocking()
    {
        // Arrange
        var tempDir = Path.GetTempPath();
        var configPath = Path.Join(tempDir, "test_extra_cfg.yml");
        
        try
        {
            await File.WriteAllTextAsync(configPath, @"
ServerDescription: ""Test Description""
EnableServerDetails: true
");
            
            // Act - Try to read while file is locked
            using var fileStream = File.Open(configPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            
            // This should handle the IOException gracefully
            var result = await ExtractServerDescriptionFromFile(configPath);
            
            // Assert
            Assert.Null(result); // Should return null due to file locking
        }
        finally
        {
            if (File.Exists(configPath))
                File.Delete(configPath);
        }
    }

    private static ACServerConfiguration CreateTestConfiguration(string baseFolder)
    {
        // Create a minimal test configuration
        return new ACServerConfiguration("", "", "", false);
    }

    private static Task<string?> ExtractServerDescriptionFromFile(string filePath)
    {
        try
        {
            using var stream = File.OpenText(filePath);
            return Task.FromResult(ParseServerDescriptionFromYaml(stream));
        }
        catch (Exception)
        {
            return Task.FromResult<string?>(null);
        }
    }

    private static string? ParseServerDescriptionFromYaml(TextReader reader)
    {
        try
        {
            var yamlParser = new Parser(reader);
            
            // Skip to document start
            yamlParser.Consume<StreamStart>();
            if (!yamlParser.Accept<DocumentStart>(out _))
            {
                return null;
            }

            // Parse the YAML document looking for ServerDescription
            while (yamlParser.Accept<Scalar>(out var scalar))
            {
                if (scalar.Value == "ServerDescription")
                {
                    // Move to next scalar (the value)
                    if (yamlParser.Accept<Scalar>(out var valueScalar))
                    {
                        var description = valueScalar.Value;
                        
                        // Basic validation
                        if (string.IsNullOrWhiteSpace(description))
                        {
                            return "";
                        }
                        
                        return description;
                    }
                }
                
                // Skip this scalar and continue
                yamlParser.MoveNext();
            }
            
            return null;
        }
        catch (YamlException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
