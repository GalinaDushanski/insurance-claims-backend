using System.Globalization;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Claims.ModelBinding;

// Query parameters do not pass through JSON converters.
public class UtcDateTimeModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var name = string.IsNullOrEmpty(bindingContext.ModelName)
            ? bindingContext.FieldName : bindingContext.ModelName;
        var value = bindingContext.ValueProvider.GetValue(name);
        bindingContext.ModelState.SetModelValue(name, value);
        if (DateTimeOffset.TryParse(value.FirstValue, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var timestamp))
        {
            bindingContext.Result = ModelBindingResult.Success(timestamp.UtcDateTime);
        }
        else
        {
            bindingContext.ModelState.TryAddModelError(name,
                "A valid date is required. Dates without a timezone are interpreted as UTC.");
        }

        return Task.CompletedTask;
    }
}
