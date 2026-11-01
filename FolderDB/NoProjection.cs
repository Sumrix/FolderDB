namespace FolderDB;

/// <summary>
/// Represents the projection type of a collection defined without a projection.
/// </summary>
/// <remarks>
/// A collection gets this type from the <c>WithoutProjection</c> builder step. It carries no data, so
/// the projection index of such a collection holds one entry per record and nothing to read from it.
/// </remarks>
public readonly record struct NoProjection;
