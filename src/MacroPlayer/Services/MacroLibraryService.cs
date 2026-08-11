using MacroCore.Models;
using MacroCore.Security;
using MacroCore.Serialization;

namespace MacroPlayer.Services;

public sealed class MacroLibraryItem
{
    public required string FullPath { get; init; }
    public required string FileName { get; init; }
    public required DateTime CreatedLocal { get; init; }
    public MacroFile? Macro { get; init; }
    public string? Error { get; init; }
    public bool IsValid => Macro is not null && Error is null;
    public string DisplayName => IsValid ? FileName : $"{FileName}  [無法載入]";
}

public sealed class MacroLibraryService
{
    public IReadOnlyList<MacroLibraryItem> Scan()
    {
        Directory.CreateDirectory(RecordingLibraryPaths.CanonicalRecordingsDirectory);
        var paths = RecordingLibraryPaths.GetSearchDirectories()
            .Where(Directory.Exists)
            .SelectMany(path => SafeEnumerate(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => File.GetCreationTimeUtc(path))
            .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return paths.Select(LoadSingle).ToArray();
    }

    public MacroLibraryItem LoadSingle(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var created = File.Exists(fullPath) ? File.GetCreationTime(fullPath) : DateTime.MinValue;
        if (!MacroSerializer.TryLoad(fullPath, out var macro, out var error))
        {
            return new MacroLibraryItem
            {
                FullPath = fullPath,
                FileName = Path.GetFileName(fullPath),
                CreatedLocal = created,
                Error = error ?? "巨集檔案不存在、空白、損壞或 schemaVersion 不支援。"
            };
        }
        return new MacroLibraryItem
        {
            FullPath = fullPath,
            FileName = Path.GetFileName(fullPath),
            CreatedLocal = created,
            Macro = macro
        };
    }

    private static IEnumerable<string> SafeEnumerate(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.macro", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
