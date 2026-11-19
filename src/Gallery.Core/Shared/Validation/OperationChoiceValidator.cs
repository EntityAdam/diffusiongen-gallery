namespace Gallery.Core.Shared.Validation;

internal static class OperationChoiceValidator
{
    public static void Validate<TChoice>(TChoice choice) where TChoice : struct, Enum
    {
        if (!Enum.IsDefined(choice))
            throw new InvalidOperationException(
                $"{typeof(TChoice).Name} value {Convert.ToInt32(choice)} is invalid. Choose {string.Join(", ", Enum.GetNames<TChoice>())}.");
    }
}
