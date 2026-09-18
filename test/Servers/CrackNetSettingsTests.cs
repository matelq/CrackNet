using Godot;

namespace CrackNet.Tests;

public partial class CrackNetSettingsTests : TestSuite
{
    [Test]
    public void FallsBackToUpstreamDefaults()
    {
        // Settings left at their registered default are not written to project.godot, so most keys are absent at runtime.
        var settings = CrackNetSettings.Load();

        Expect.Equal(30, settings.Tickrate);
        Expect.Equal("127.0.0.1", settings.AutoconnectHost);
        Expect.True(settings.EventsEnabled);

        // Upstream reads cracknet/time/recalibrate_threshold with two different fallbacks; they only differ while it is unset.
        Expect.Equal(8.0, settings.RecalibrateThreshold);
        Expect.Equal(2.0, settings.SyncPanicThreshold);
    }

    [Test]
    public void ReadsChangedSettings()
    {
        var original = ProjectSettings.GetSetting("cracknet/time/tickrate");
        try
        {
            ProjectSettings.SetSetting("cracknet/time/tickrate", 77);
            Expect.Equal(77, CrackNetSettings.Load().Tickrate);
        }
        finally
        {
            // The key was absent to begin with; assigning null deletes it again.
            ProjectSettings.SetSetting("cracknet/time/tickrate", original);
        }
    }
}
