namespace MacroCore.Input;

public static class InputSyntheticMarker
{
    public const ulong NumericValue = 0x4D4B4D4143524F31UL;
    public static UIntPtr Value => new(NumericValue);

    public static bool IsOwn(UIntPtr extraInfo) => extraInfo.ToUInt64() == NumericValue;
    public static bool IsOwn(ulong extraInfo) => extraInfo == NumericValue;
}
