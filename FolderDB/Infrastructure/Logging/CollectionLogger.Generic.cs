using Microsoft.Extensions.Logging;

namespace FolderDB.Infrastructure.Logging;

internal sealed class CollectionLogger<T>(ILogger<T> inner, string collectionName) : CollectionLogger(inner, collectionName), ILogger<T>
{
}
