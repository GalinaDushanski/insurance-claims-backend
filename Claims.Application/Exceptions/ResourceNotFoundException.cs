namespace Claims.Exceptions;

public class ResourceNotFoundException(string resource, string id)
    : Exception($"{resource} '{id}' was not found.")
{
}
