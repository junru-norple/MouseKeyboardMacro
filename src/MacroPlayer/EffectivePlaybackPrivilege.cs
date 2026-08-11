namespace MacroPlayer;

public enum EffectivePlaybackPrivilegeRequirement
{
    Standard,
    Administrator,
    Unknown
}

public enum PrivilegeMetadataConsistency
{
    Consistent,
    Legacy,
    ConflictingHighMetadata,
    Incomplete
}

public sealed record EffectivePlaybackPrivilegeResolution(
    EffectivePlaybackPrivilegeRequirement Requirement,
    PrivilegeMetadataConsistency Consistency,
    string Reason)
{
    public bool RequiresAdministrator => Requirement == EffectivePlaybackPrivilegeRequirement.Administrator;
}

public static class EffectivePlaybackPrivilegeResolver
{
    public const string AdministratorMessage = "此巨集由管理員 Recorder 錄製，必須使用管理員 Player。";
    public const string ConflictMessage = "檔案的舊權限欄位互相矛盾；基於安全已要求管理員播放器。";

    public static EffectivePlaybackPrivilegeResolution Resolve(PlaybackMacroDocument macro)
    {
        ArgumentNullException.ThrowIfNull(macro);

        bool recorderHigh = IsHigh(macro.RecordedRecorderIntegrity) || IsHigh(macro.LegacyRecorderIntegrity);
        bool targetHigh = IsHigh(macro.RecordedTargetIntegrity) || IsHigh(macro.LegacyTargetIntegrity);
        bool elevatedSession = IsElevatedMarker(macro.CaptureSessionMode) || IsElevatedMarker(macro.CaptureMode);
        bool highMetadata = recorderHigh || targetHigh || elevatedSession;

        if (macro.RequiresElevation == true || highMetadata)
        {
            PrivilegeMetadataConsistency consistency = macro.RequiresElevation == false && highMetadata
                ? PrivilegeMetadataConsistency.ConflictingHighMetadata
                : PrivilegeMetadataConsistency.Consistent;
            return new EffectivePlaybackPrivilegeResolution(
                EffectivePlaybackPrivilegeRequirement.Administrator,
                consistency,
                consistency == PrivilegeMetadataConsistency.ConflictingHighMetadata ? ConflictMessage : AdministratorMessage);
        }

        bool hasIntegrityMetadata = HasValue(macro.RecordedRecorderIntegrity) ||
                                    HasValue(macro.LegacyRecorderIntegrity) ||
                                    HasValue(macro.RecordedTargetIntegrity) ||
                                    HasValue(macro.LegacyTargetIntegrity);
        if (macro.SchemaVersion.Equals("1.0", StringComparison.Ordinal) && !hasIntegrityMetadata)
        {
            return new EffectivePlaybackPrivilegeResolution(
                EffectivePlaybackPrivilegeRequirement.Unknown,
                PrivilegeMetadataConsistency.Legacy,
                "權限需求未知");
        }

        if (macro.RequiresElevation == false && hasIntegrityMetadata)
        {
            return new EffectivePlaybackPrivilegeResolution(
                EffectivePlaybackPrivilegeRequirement.Standard,
                PrivilegeMetadataConsistency.Consistent,
                "一般權限播放器可使用此巨集。");
        }

        return new EffectivePlaybackPrivilegeResolution(
            EffectivePlaybackPrivilegeRequirement.Unknown,
            PrivilegeMetadataConsistency.Incomplete,
            "權限需求未知");
    }

    public static bool CanStart(PlaybackMacroDocument macro, bool playerElevated) =>
        playerElevated || Resolve(macro).Requirement != EffectivePlaybackPrivilegeRequirement.Administrator;

    private static bool IsHigh(string? value) =>
        value is not null && (value.Equals("High", StringComparison.OrdinalIgnoreCase) ||
                              value.Equals("System", StringComparison.OrdinalIgnoreCase) ||
                              value.Equals("Administrator", StringComparison.OrdinalIgnoreCase));

    private static bool IsElevatedMarker(string? value) =>
        value is not null && (value.Contains("elevated", StringComparison.OrdinalIgnoreCase) ||
                              value.Contains("administrator", StringComparison.OrdinalIgnoreCase) ||
                              value.Contains("high-integrity", StringComparison.OrdinalIgnoreCase));

    private static bool HasValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
}

public sealed record PlayerPrivilegeUiDecision(bool StartEnabled, bool ElevateVisible, string Message);

public static class PlayerPrivilegeUiPolicy
{
    public static PlayerPrivilegeUiDecision Resolve(PlaybackMacroDocument? macro, bool playerElevated)
    {
        if (macro is null)
        {
            return new PlayerPrivilegeUiDecision(false, false, "請選擇巨集。");
        }
        EffectivePlaybackPrivilegeResolution privilege = EffectivePlaybackPrivilegeResolver.Resolve(macro);
        bool blocked = privilege.Requirement == EffectivePlaybackPrivilegeRequirement.Administrator && !playerElevated;
        bool elevate = !playerElevated && privilege.Requirement != EffectivePlaybackPrivilegeRequirement.Standard;
        return new PlayerPrivilegeUiDecision(!blocked, elevate, privilege.Reason);
    }
}
