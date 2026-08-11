using System.Text;
using System.Text.Json;

namespace MacroCore.Tests;

internal static class SyntheticMacroFixtureFactory
{
    private static readonly object Sync = new();
    private static readonly Lazy<string> FixtureRoot = new(() =>
    {
        string root = Path.Combine(ProjectLocalTestSandbox.Create(), "runtime-generated-fixtures");
        Directory.CreateDirectory(root);
        return root;
    });

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string GetPath(string name)
    {
        string safeName = Path.GetFileName(name);
        if (!safeName.Equals(name, StringComparison.Ordinal) || !safeName.EndsWith(".macro", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Synthetic fixture name is invalid.", nameof(name));
        }

        string path = Path.Combine(FixtureRoot.Value, safeName);
        lock (Sync)
        {
            File.WriteAllText(path, GetText(safeName), new UTF8Encoding(false));
        }
        return path;
    }

    public static string GetText(string name) => name switch
    {
        "AutoRepeat173.macro" => AutoRepeatJson(),
        "SyntheticSparseCapture.macro" => SparseJson(),
        "SyntheticRawDual.macro" => RawDualJson(),
        "SyntheticAdmin.macro" => PrivilegeJson(true),
        "SyntheticOrdinary.macro" => PrivilegeJson(false),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown synthetic fixture.")
    };

    private static string AutoRepeatJson()
    {
        List<object> events = [];
        for (int index = 0; index < 172; index++)
        {
            events.Add(new Dictionary<string, object?>
            {
                ["type"] = "KeyDown", ["timeMs"] = index * 10, ["virtualKey"] = 65,
                ["scanCode"] = 30, ["isExtended"] = false, ["flags"] = 0
            });
        }
        events.Add(new Dictionary<string, object?>
        {
            ["type"] = "KeyUp", ["timeMs"] = 1720, ["virtualKey"] = 65,
            ["scanCode"] = 30, ["isExtended"] = false, ["flags"] = 0
        });
        return SerializeBase("synthetic_auto_repeat_173", 1720, events, "SyntheticFixture");
    }

    private static string SparseJson()
    {
        object[] events =
        [
            new Dictionary<string, object?> { ["type"] = "MouseDown", ["timeMs"] = 60000, ["x"] = 320, ["y"] = 240, ["mouseButton"] = "Left", ["flags"] = 0 },
            new Dictionary<string, object?> { ["type"] = "MouseUp", ["timeMs"] = 60100, ["x"] = 320, ["y"] = 240, ["mouseButton"] = "Left", ["flags"] = 0 }
        ];
        return SerializeBase("synthetic_sparse_capture", 120000, events, "SyntheticFixture");
    }

    private static string RawDualJson()
    {
        var document = new Dictionary<string, object?>
        {
            ["schemaVersion"] = "1.2",
            ["name"] = "Synthetic Raw Dual Fixture",
            ["createdAt"] = "2000-01-01T00:00:00Z",
            ["durationMs"] = 20,
            ["recordedDisplayLayout"] = DisplayLayout(),
            ["events"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "MouseMove", ["timeMs"] = 0, ["x"] = 100, ["y"] = 120, ["deltaX"] = 1, ["deltaY"] = -1, ["captureSource"] = "RawMouse", ["mouseMovementMode"] = "RawRelative", ["isInitialCursorAnchor"] = true },
                new Dictionary<string, object?> { ["type"] = "MouseMove", ["timeMs"] = 20, ["x"] = 104, ["y"] = 118, ["deltaX"] = 4, ["deltaY"] = -2, ["captureSource"] = "RawMouse", ["mouseMovementMode"] = "RawRelative" }
            },
            ["captureMetadata"] = Metadata("RawEnhanced")
        };
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    private static string PrivilegeJson(bool administrator) => SerializeBase(
        administrator ? "synthetic_administrator" : "synthetic_ordinary",
        100,
        [
            new Dictionary<string, object?>
            {
                ["type"] = "KeyDown", ["timeMs"] = 0, ["virtualKey"] = 65,
                ["scanCode"] = 30, ["isExtended"] = false, ["flags"] = 0
            },
            new Dictionary<string, object?>
            {
                ["type"] = "KeyUp", ["timeMs"] = 100, ["virtualKey"] = 65,
                ["scanCode"] = 30, ["isExtended"] = false, ["flags"] = 0
            }
        ],
        "Standard",
        administrator,
        administrator ? "High" : "Medium",
        administrator ? "High" : "Medium");

    private static string SerializeBase(
        string name,
        long duration,
        IEnumerable<object> events,
        string captureMode,
        bool requiresElevation = false,
        string recorderIntegrity = "Medium",
        string targetIntegrity = "Medium")
    {
        var document = new Dictionary<string, object?>
        {
            ["schemaVersion"] = "1.2",
            ["macroName"] = name,
            ["createdAt"] = "2000-01-01T00:00:00Z",
            ["duration"] = duration,
            ["requiresElevationForPlayback"] = requiresElevation,
            ["recordedRecorderIntegrity"] = recorderIntegrity,
            ["recordedTargetIntegrity"] = targetIntegrity,
            ["recordedDisplayLayout"] = DisplayLayout(),
            ["captureMetadata"] = Metadata(captureMode, requiresElevation, recorderIntegrity, targetIntegrity),
            ["events"] = events
        };
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    private static object DisplayLayout() => new Dictionary<string, object?>
    {
        ["virtualBounds"] = Bounds(),
        ["screens"] = new object[]
        {
            new Dictionary<string, object?> { ["deviceName"] = "DISPLAY_SYNTHETIC", ["bounds"] = Bounds(), ["isPrimary"] = true, ["dpiX"] = 96, ["dpiY"] = 96 }
        },
        ["primaryScreenBounds"] = Bounds(),
        ["screenCount"] = 1
    };

    private static object Bounds() => new Dictionary<string, object?>
    {
        ["x"] = 0, ["y"] = 0, ["width"] = 1280, ["height"] = 720
    };

    private static object Metadata(
        string mode,
        bool requiresElevation = false,
        string recorderIntegrity = "Medium",
        string targetIntegrity = "Medium") => new Dictionary<string, object?>
    {
        ["captureMode"] = mode,
        ["targetProcessName"] = "SyntheticTarget.exe",
        ["requiresElevationForPlayback"] = requiresElevation,
        ["recordedRecorderIntegrity"] = recorderIntegrity,
        ["recorderIntegrity"] = recorderIntegrity,
        ["recordedTargetIntegrity"] = targetIntegrity,
        ["targetIntegrity"] = targetIntegrity,
        ["recordedWithVersion"] = "synthetic-generator-v2"
    };
}
