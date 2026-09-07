using System;
using System.Collections.Generic;
using System.IO;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

public sealed class FileShelfControllerSelectionTests : IDisposable
{
    private readonly string _fileA = Path.Combine(Path.GetTempPath(), $"vnotch-sel-a-{Guid.NewGuid():N}.txt");
    private readonly string _fileB = Path.Combine(Path.GetTempPath(), $"vnotch-sel-b-{Guid.NewGuid():N}.txt");
    private readonly string _fileC = Path.Combine(Path.GetTempPath(), $"vnotch-sel-c-{Guid.NewGuid():N}.txt");

    public FileShelfControllerSelectionTests()
    {
        File.WriteAllText(_fileA, "a");
        File.WriteAllText(_fileB, "b");
        File.WriteAllText(_fileC, "c");
    }

    [Fact]
    public void ApplyRectangleSelection_WithoutCtrl_SelectsOnlyIntersected()
    {
        using var controller = CreateController();
        Assert.True(controller.AddFileDirect(_fileA));
        Assert.True(controller.AddFileDirect(_fileB));
        Assert.True(controller.AddFileDirect(_fileC));

        var intersected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _fileA, _fileC };
        var initialState = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _fileB };

        controller.ApplyRectangleSelection(intersected, isCtrl: false, initialState);

        Assert.True(controller.IsSelected(_fileA));
        Assert.False(controller.IsSelected(_fileB));
        Assert.True(controller.IsSelected(_fileC));
    }

    [Fact]
    public void ApplyRectangleSelection_WithCtrl_InvertsIntersectedAndPreservesNonIntersected()
    {
        using var controller = CreateController();
        Assert.True(controller.AddFileDirect(_fileA));
        Assert.True(controller.AddFileDirect(_fileB));
        Assert.True(controller.AddFileDirect(_fileC));

        // Initially, fileA and fileB were selected
        var initialState = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _fileA, _fileB };
        // Selection rectangle covers fileB and fileC
        var intersected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _fileB, _fileC };

        controller.ApplyRectangleSelection(intersected, isCtrl: true, initialState);

        // fileA was in initialState and NOT intersected -> stays selected
        Assert.True(controller.IsSelected(_fileA));
        // fileB was in initialState and intersected -> inverted to unselected
        Assert.False(controller.IsSelected(_fileB));
        // fileC was NOT in initialState and intersected -> inverted to selected
        Assert.True(controller.IsSelected(_fileC));
    }

    public void Dispose()
    {
        if (File.Exists(_fileA)) File.Delete(_fileA);
        if (File.Exists(_fileB)) File.Delete(_fileB);
        if (File.Exists(_fileC)) File.Delete(_fileC);
    }

    private static FileShelfController CreateController() =>
        new(new NotchSettings { IsShelfUploadLimitUnlocked = true }, new FakeSettingsService());
}
