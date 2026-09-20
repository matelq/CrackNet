using Godot;

namespace CrackNet.Tests;

public partial class CrackNetSettingsTests : TestSuite
{
    [Test]
    public void FallsBackToUpstreamDefaults()
    {
        // Settings left at their registered default are not written to project.godot, so most keys are absent at runtime.
        var settings = CrackNetSettings.Load();

        Expect.Equal(2, settings.StateIntervalTicks);
        Expect.Equal("127.0.0.1", settings.AutoconnectHost);
        Expect.True(settings.EventsEnabled);
        Expect.Equal(8.0, settings.RecalibrateThreshold);
    }

    [Test]
    public void ReadsChangedSettings()
    {
        var original = ProjectSettings.GetSetting("cracknet/time/state_interval_ticks");
        try
        {
            ProjectSettings.SetSetting("cracknet/time/state_interval_ticks", 4);
            Expect.Equal(4, CrackNetSettings.Load().StateIntervalTicks);
        }
        finally
        {
            // The key was absent to begin with; assigning null deletes it again.
            ProjectSettings.SetSetting("cracknet/time/state_interval_ticks", original);
        }
    }
}
