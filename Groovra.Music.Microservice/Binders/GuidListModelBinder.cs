using System.Text.Json;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Groovra.Music.Microservice.Binders;

public class GuidListModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var valueProviderResult = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);

        if (valueProviderResult == ValueProviderResult.None)
        {
            bindingContext.Result = ModelBindingResult.Success(new List<Guid>());
            return Task.CompletedTask;
        }

        var rawValues = valueProviderResult.Values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

        var result = new List<Guid>();

        if (rawValues.Count == 1 && rawValues[0].TrimStart().StartsWith("["))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<string>>(rawValues[0]);
                if (parsed != null)
                    result.AddRange(parsed.Where(s => Guid.TryParse(s, out _)).Select(Guid.Parse));
            }
            catch (JsonException)
            {
                bindingContext.ModelState.TryAddModelError(
                    bindingContext.ModelName, "Некорректный JSON-массив в TrackIds.");
                bindingContext.Result = ModelBindingResult.Failed(); 
                return Task.CompletedTask; 
            }
        }
        else
        {
            foreach (var raw in rawValues)
            {
                foreach (var piece in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (Guid.TryParse(piece, out var guid))
                        result.Add(guid);
                }
            }
        }

        bindingContext.Result = ModelBindingResult.Success(result);
        return Task.CompletedTask;
    }
}

public class GuidListModelBinderProvider : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        if (context.Metadata.ModelType == typeof(List<Guid>))
            return new GuidListModelBinder();

        return null;
    }
}