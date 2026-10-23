using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class SettingsEditSessionTests
{
    [Fact]
    public void PreviewOwnsSnapshotsAndCancelRestoresCommittedSettings()
    {
        var original = new NotchSettings { Width = 300 };
        var session = new SettingsEditSession(original);
        original.Width = 999;
        var edited = session.Applied;
        edited.Width = 500;
        Assert.True(session.Preview(edited));
        Assert.False(session.Preview(edited.Clone()));
        edited.Width = 1000;
        Assert.Equal(500, session.Applied.Width);
        Assert.Equal(300, session.Revert().Width);
        Assert.Equal(300, session.Applied.Width);
    }

    [Fact]
    public void ExternalInvisibleCommandPersistsOnlyItsSettingAndNewSaveBecomesCancelBaseline()
    {
        var session = new SettingsEditSession(new NotchSettings { Width = 300 });
        var edited = session.Applied;
        edited.Width = 500;
        session.Preview(edited);
        var persisted = session.CommitInvisibleMode(true);
        Assert.True(persisted.InvisibleMode);
        Assert.Equal(300, persisted.Width);
        Assert.Equal(500, session.Applied.Width);
        Assert.True(session.Revert().InvisibleMode);
        edited.InvisibleMode = true;
        session.Commit(edited);
        Assert.Equal(500, session.Revert().Width);
    }
}
