namespace Jibo.Cloud.Application.Services;

internal static class NativeConversationValue
{
    internal static object? Read(IDictionary<string, object?>? dictionary, string key) =>
        dictionary is not null && dictionary.TryGetValue(key, out var value) ? value : null;
}
