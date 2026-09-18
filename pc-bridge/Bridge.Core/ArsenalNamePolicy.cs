namespace PhantomDust.PcBridge.Core;

public static class ArsenalNamePolicy {
    public const int MaxLength = 15;
    public const string Message = "Use an arsenal name with 1–15 ASCII letters, numbers, or spaces, including at least one letter or number.";

    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= MaxLength &&
        value.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9') &&
        value.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or ' ');
}
