using MacroCore.Models;
using MacroCore.Serialization;
using MacroCore.Timing;

namespace MacroCore.Tests;

public class SerializationTests
{
    [Fact]
    public void Macro_Serialization_RoundTrip()
    {
        var macro = new MacroFile
        {
            MacroName = "Test",
            CreatedAt = DateTimeOffset.UtcNow,
            DurationMs = 1000,
            RecordedDisplayLayout = new MacroDisplayLayout
            {
                ScreenCount = 1,
                VirtualBounds = new MacroRect { X = 0, Y = 0, Width = 1920, Height = 1080 },
                PrimaryScreenBounds = new MacroRect { X = 0, Y = 0, Width = 1920, Height = 1080 },
                Screens =
                [
                    new MacroScreenInfo
                    {
                        DeviceName = "DISPLAY1",
                        Bounds = new MacroRect { X = 0, Y = 0, Width = 1920, Height = 1080 },
                        IsPrimary = true,
                        DpiX = 96,
                        DpiY = 96
                    }
                ]
            },
            Events =
            {
                new MacroEventRecord
                {
                    Type = MacroEventKind.KeyDown,
                    TimeMs = 10,
                    VirtualKey = 0x41,
                    ScanCode = 0x1E,
                    IsExtended = false
                },
                new MacroEventRecord
                {
                    Type = MacroEventKind.KeyUp,
                    TimeMs = 120,
                    VirtualKey = 0x41,
                    ScanCode = 0x1E,
                    IsExtended = false
                }
            }
        };

        var json = MacroSerializer.ToJson(macro);
        var loaded = MacroSerializer.FromJson(json);

        Assert.Equal(macro.MacroName, loaded.MacroName);
        Assert.Equal(macro.Events.Count, loaded.Events.Count);
        Assert.Equal(macro.Events[0].Type, loaded.Events[0].Type);
    }

    [Fact]
    public void Macro_SaveLoad_Schema_Validate()
    {
        var temp = Path.Combine(ProjectLocalTestSandbox.Create(), $"macro_{Guid.NewGuid():N}.json");
        try
        {
            MacroSerializer.SaveAtomically(new MacroFile(), temp);
            Assert.True(MacroSerializer.TryLoad(temp, out var loaded, out var _));
            Assert.NotNull(loaded);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    [Fact]
    public void Macro_Schema_Version_UnknownRejected()
    {
        var badJson = """
{
    "schemaVersion":"0.0",
    "macroName":"bad",
    "createdAt":"2026-01-01T00:00:00Z",
    "duration":0,
    "recordedDisplayLayout":{"virtualBounds":{"x":0,"y":0,"width":1,"height":1},"screens":[],"primaryScreenBounds":{"x":0,"y":0,"width":1,"height":1},"screenCount":0},
    "events":[]
}
""";
        var path = Path.Combine(ProjectLocalTestSandbox.Create(), $"macro_bad_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, badJson);
            Assert.False(MacroSerializer.TryLoad(path, out _, out var err));
            Assert.Contains("schemaversion", (err ?? string.Empty).ToLowerInvariant());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Macro_Validate_Unmatched_KeyPairs()
    {
        var macro = new MacroFile();
        macro.Events.Add(new MacroEventRecord
        {
            Type = MacroEventKind.KeyUp,
            TimeMs = 10,
            VirtualKey = 65,
            ScanCode = 30
        });

        var path = Path.Combine(ProjectLocalTestSandbox.Create(), $"macro_pair_{Guid.NewGuid():N}.json");
        try
        {
            var json = MacroSerializer.ToJson(macro);
            File.WriteAllText(path, json);
            Assert.False(MacroSerializer.TryLoad(path, out _, out var err));
            Assert.Contains("keyup", (err ?? string.Empty).ToLowerInvariant());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
