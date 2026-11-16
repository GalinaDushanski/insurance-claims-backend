namespace Claims.Validation;

public class BusinessValidationException(Dictionary<string, string[]> errors)
    : Exception("One or more validation errors occurred.")
{
    public Dictionary<string, string[]> Errors { get; } = errors;
}
