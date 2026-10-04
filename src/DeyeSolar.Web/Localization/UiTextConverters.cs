using System.Runtime.CompilerServices;
using System.Globalization;
using MudBlazor;

namespace DeyeSolar.Web.Localization;

/// <summary>Keeps each input's conversion state independent while translating MudBlazor's conversion errors.</summary>
public static class UiTextConverters
{
    private static readonly ConditionalWeakTable<object, Dictionary<string, object>> Inputs = new();

    public static Converter<TValue> InputConverter<TValue>(this UiText text, object inputOwner, string inputName, string? format = null)
    {
        var inputs = Inputs.GetOrCreateValue(inputOwner);
        lock (inputs)
        {
            if (inputs.TryGetValue(inputName, out var existing)) return (Converter<TValue>)existing;
            // The time picker configures custom delegates around its HH:mm format.
            var converter = typeof(TValue) == typeof(TimeSpan?)
                ? (Converter<TValue>)(object)new MudTimePicker().Converter
                : new DefaultConverter<TValue> { Format = format };
            converter.Culture = CultureInfo.CurrentCulture;
            var convert = converter.GetFunc!;
            converter.GetFunc = value =>
            {
                var result = convert(value);
                if (converter.GetError) converter.GetErrorMessage = text.Translate(converter.GetErrorMessage);
                return result;
            };
            inputs[inputName] = converter;
            return converter;
        }
    }
}
