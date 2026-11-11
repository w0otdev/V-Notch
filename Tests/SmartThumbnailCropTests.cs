using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using VNotch.Services;
using Xunit;
using Detection = VNotch.Services.SmartThumbnailCropService.Detection;

namespace VNotch.Tests;

public sealed partial class SmartThumbnailCropTests
{
    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_smartcrop_apply_nms(
        Detection* inDetections, int inCount,
        Detection* outDetections, int outCap, out int outCount);

    private static unsafe List<Detection> ApplyNms(List<Detection> detections)
    {
        ReadOnlySpan<Detection> inSpan = CollectionsMarshal.AsSpan(detections);
        Detection* outPtr = stackalloc Detection[Math.Max(1, detections.Count)];
        int outCount;
        fixed (Detection* inPtr = inSpan)
        {
            Assert.Equal(0, vn_smartcrop_apply_nms(inPtr, inSpan.Length, outPtr, detections.Count, out outCount));
        }
        var result = new List<Detection>(outCount);
        for (int i = 0; i < outCount; i++) result.Add(outPtr[i]);
        return result;
    }
    [Fact]
    public void PortraitFramingLeavesHairAndUpperTorsoSpace()
    {
        var face = new Detection { X1 = 1160, Y1 = 211, X2 = 1443, Y2 = 656 };
        var crop = SmartThumbnailCropService.GetPortraitCropRect(face, 2080, 1121);

        Assert.True(crop.Y < 62); // Top of the hair in the reported portrait.
        Assert.True(crop.Y + crop.Height > 900); // Include shoulders and upper torso.
        Assert.True(crop.X > 800); // Leave the title artwork outside the frame.
        Assert.True(crop.X + crop.Width > 1650); // Preserve the back of the hair.
        Assert.Equal(crop.Width, crop.Height);
    }

    [Theory]
    [InlineData(0, 0, 100, 120)]
    [InlineData(500, 180, 600, 300)]
    public void PortraitFramingStaysInsideImage(float x1, float y1, float x2, float y2)
    {
        var face = new Detection { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };
        var crop = SmartThumbnailCropService.GetPortraitCropRect(face, 600, 300);

        Assert.InRange(crop.X, 0, 600 - crop.Width);
        Assert.InRange(crop.Y, 0, 300 - crop.Height);
        Assert.Equal(crop.Width, crop.Height);
    }

    [Fact]
    public void NmsKeepsOverlappingSubjectsOfDifferentClasses()
    {
        var boxes = new List<Detection>
        {
            new() { X1 = 10, Y1 = 10, X2 = 110, Y2 = 110, Confidence = 0.9f, ClassId = 0 },
            new() { X1 = 10, Y1 = 10, X2 = 110, Y2 = 110, Confidence = 0.8f, ClassId = 16 }
        };

        Assert.Equal(2, ApplyNms(boxes).Count);
    }

    [Fact]
    public void NmsReliablyKeepsBestBoxAndRemovesSameClassDuplicates()
    {
        var boxes = new List<Detection>
        {
            new() { X1 = 10, Y1 = 10, X2 = 110, Y2 = 110, Confidence = 0.6f, ClassId = 0 },
            new() { X1 = 12, Y1 = 12, X2 = 112, Y2 = 112, Confidence = 0.9f, ClassId = 0 }
        };

        for (int i = 0; i < 100; i++)
            Assert.Equal(0.9f, Assert.Single(ApplyNms(boxes)).Confidence);
    }

    [Fact]
    public void PortraitCropPreservesHeadWhenFullBodyCannotFit()
    {
        var person = new Detection { X1 = 80, Y1 = 40, X2 = 220, Y2 = 550, Confidence = 0.9f };
        var crop = SmartThumbnailCropService.GetSinglePersonCropRect(person, 300, 600, 300);

        Assert.InRange(crop.Y, 0, (int)person.Y1);
        Assert.Equal(300, crop.Width);
        Assert.Equal(crop.Width, crop.Height);
        Assert.True(crop.Y + crop.Height <= 600);
    }

    [Fact]
    public void LandscapeCropKeepsWholePersonWhenTheyFit()
    {
        var person = new Detection { X1 = 350, Y1 = 30, X2 = 470, Y2 = 270, Confidence = 0.9f };
        var crop = SmartThumbnailCropService.GetSinglePersonCropRect(person, 600, 300, 300);

        Assert.True(crop.X <= person.X1 && crop.X + crop.Width >= person.X2);
        Assert.True(crop.Y <= person.Y1 && crop.Y + crop.Height >= person.Y2);
    }
}
