using System;
using System.IO;
using System.Threading.Tasks;
using Mainguard.Agents.Services;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Xunit;

namespace Mainguard.Tests;

public class SettingsServiceTests : IDisposable
{
    private readonly string _tempFile;

    public SettingsServiceTests()
    {
        // Use a unique temp file for each test run to prevent cross-test contamination
        _tempFile = Path.Combine(Path.GetTempPath(), $"mainguard_settings_test_{Guid.NewGuid()}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            try
            {
                File.Delete(_tempFile);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        var tempTmpFile = _tempFile + ".tmp";
        if (File.Exists(tempTmpFile))
        {
            try
            {
                File.Delete(tempTmpFile);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public void Constructor_ShouldInitializeWithDefaults_WhenFileDoesNotExist()
    {
        // Arrange & Act
        var service = new SettingsService(_tempFile);

        // Assert
        Assert.NotNull(service.Current);
        Assert.Equal("MidnightLoom", service.Current.Theme);
        // Vibrancy is opt-in: a fresh install gets the flat opaque chrome.
        Assert.False(service.Current.MacTranslucentChrome);
        // App-lifecycle defaults: X hides to the tray; a full exit stops the VM.
        Assert.True(service.Current.CloseToTray);
        Assert.True(service.Current.StopVmOnExit);
    }

    [Fact]
    public void LifecycleSettings_RoundTripThroughDisk()
    {
        var service = new SettingsService(_tempFile);
        service.Update(p =>
        {
            p.CloseToTray = false;
            p.StopVmOnExit = false;
        });

        var reloaded = new SettingsService(_tempFile);
        Assert.False(reloaded.Current.CloseToTray);
        Assert.False(reloaded.Current.StopVmOnExit);
    }

    [Fact]
    public void Save_ShouldWritePreferencesToFile()
    {
        // Arrange
        var service = new SettingsService(_tempFile);

        // Act
        service.Update(prefs =>
        {
            prefs.Theme = "Light";
            prefs.MacTranslucentChrome = true;
        });

        // Assert
        Assert.True(File.Exists(_tempFile));

        var service2 = new SettingsService(_tempFile);
        Assert.Equal("Light", service2.Current.Theme);
        Assert.True(service2.Current.MacTranslucentChrome);
    }

    [Fact]
    public void Load_ShouldFallbackToDefaults_WhenFileContainsInvalidJson()
    {
        // Arrange
        File.WriteAllText(_tempFile, "INVALID JSON CONTENT");

        // Act
        var service = new SettingsService(_tempFile);

        // Assert
        Assert.NotNull(service.Current);
        Assert.Equal("MidnightLoom", service.Current.Theme);
        Assert.False(service.Current.MacTranslucentChrome);
    }

    [Fact]
    public void Update_ShouldBeThreadSafeAndCorrect()
    {
        // Arrange
        var service = new SettingsService(_tempFile);
        int iterations = 100;

        // Act
        Parallel.For(0, iterations, i =>
        {
            service.Update(prefs =>
            {
                prefs.Theme = $"Theme_{i}";
            });
        });

        // Assert
        Assert.StartsWith("Theme_", service.Current.Theme);
    }
}
