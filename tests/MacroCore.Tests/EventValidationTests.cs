using MacroCore.Models;
using MacroCore.Serialization;

namespace MacroCore.Tests;

public class EventValidationTests
{
    [Fact]
    public void Validate_Event_Order()
    {
        var macro = new MacroFile
        {
            Events =
            {
                new MacroEventRecord { Type = MacroEventKind.MouseMove, TimeMs = 10, X = 1, Y = 2 },
                new MacroEventRecord { Type = MacroEventKind.MouseMove, TimeMs = 5, X = 2, Y = 3 }
            }
        };

        var path = Path.Combine(ProjectLocalTestSandbox.Create(), $"macro_order_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, MacroSerializer.ToJson(macro));
            Assert.False(MacroSerializer.TryLoad(path, out _, out var err));
            Assert.Contains("time", err?.ToLowerInvariant() ?? string.Empty);
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
    public void Validate_Mouse_Pairs()
    {
        var macro = new MacroFile
        {
            Events =
            {
                new MacroEventRecord { Type = MacroEventKind.MouseDown, TimeMs = 1, MouseButton = MouseButtonKind.Left, X = 0, Y = 0 },
                new MacroEventRecord { Type = MacroEventKind.MouseMove, TimeMs = 2, X = 10, Y = 10 },
                new MacroEventRecord { Type = MacroEventKind.MouseUp, TimeMs = 3, MouseButton = MouseButtonKind.Left, X = 10, Y = 10 },
            }
        };

        var path = Path.Combine(ProjectLocalTestSandbox.Create(), $"macro_mouse_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, MacroSerializer.ToJson(macro));
            Assert.True(MacroSerializer.TryLoad(path, out _, out var err));
            Assert.Null(err);
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
    public void Validate_Bad_Atomic_Save_Cleans_Temporary_File()
    {
        var macro = new MacroFile();
        var badDirectory = Path.Combine(ProjectLocalTestSandbox.Create(), $"bad_{Guid.NewGuid():N}");
        Directory.CreateDirectory(badDirectory);
        File.SetAttributes(badDirectory, FileAttributes.ReadOnly);
        var path = Path.Combine(badDirectory, "macro.macro");

        try
        {
            Assert.ThrowsAny<Exception>(() => MacroSerializer.SaveAtomically(macro, path));
            var tempFiles = Directory.GetFiles(badDirectory, "*.tmp");
            Assert.Empty(tempFiles);
        }
        finally
        {
            File.SetAttributes(badDirectory, FileAttributes.Normal);
            if (Directory.Exists(badDirectory))
            {
                Directory.Delete(badDirectory, true);
            }
        }
    }
}
