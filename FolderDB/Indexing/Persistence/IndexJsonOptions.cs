using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace FolderDB.Indexing.Persistence;

internal static class IndexJsonOptions
{
    private static JsonSerializerOptions? _reflection;

    public static JsonSerializerOptions Reflection
    {
        [RequiresUnreferencedCode("Uses the reflection-based JSON contract resolver.")]
        [RequiresDynamicCode("Uses the reflection-based JSON contract resolver.")]
        get => _reflection ??= CreateReflection();
    }

    [RequiresUnreferencedCode("Uses the reflection-based JSON contract resolver.")]
    [RequiresDynamicCode("Uses the reflection-based JSON contract resolver.")]
    private static JsonSerializerOptions CreateReflection()
    {
        // IncludeFields makes value tuples serializable
        var options = new JsonSerializerOptions { IncludeFields = true };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
