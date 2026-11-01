# FolderDB

FolderDB is a local-first file-system database for .NET.

It is built around a simple idea: the file system itself should be a user-friendly API for the database. A collection is a folder, a record is a file, and the data remains readable, editable, movable, and inspectable with ordinary operating system tools.

## Features

FolderDB:

- treats files as database records;
- exposes a database-table-like API with primary keys;
- lets users create, edit, rename, move, and delete records directly through the file system;
- keeps records readable and inspectable with ordinary tools;
- handles delayed file-system watcher events and recovery scans;
- supports schema-versioned records and migrations;
- supports trimming and NativeAOT.

## Quick Start

Clone the repository and reference the FolderDB project directly. A NuGet package is planned for later.

```csharp
using FolderDB;

var users = CollectionDefinitionBuilder.CreateDefault<string, User>();

await using var store = await FolderStore.OpenAsync(
    path: "data",
    collectionDefinitions: [users]);

var collection = store.Collection<string, User>();

await collection.UpsertAsync(new User(
    Id: "alice",
    DisplayName: "Alice",
    Email: "alice@example.com"));

User? alice = await collection.GetAsync("alice");

await collection.DeleteAsync("alice");

public sealed record User(
    string Id,
    string DisplayName,
    string Email) : IRecord<string>;
```

This creates a database directory, creates a collection directory for `User`, writes a user record as a file, reads it back through the collection API, and then deletes it.

## Who Is It For

FolderDB is for applications where users should have direct access to their data files.

It can fit scenarios such as chat history, game saves, settings presets, editable local content, or other data that users may want to inspect, edit, back up, sync, share, or put under source control without a special database tool.

## Reliability & Data Safety

User data is sacred. FolderDB treats your files as the source of truth, and the in-memory index must never silently diverge or lose data.

Instead of relying on ad-hoc reconciliation logic, FolderDB uses a strict, explicitly modeled decision process. Every important edge case, including missing files, decode errors, ID changes, and I/O races, is handled intentionally.

[Read the Index Reconciliation Rulebook](https://github.com/Sumrix/FolderDB/blob/main/docs/index-reconciliation-rulebook.md) to see the exact decision collections and lock boundaries. Analyze it yourself: no hand-waving, no hidden magic.

## Status

FolderDB is in active development. The project works, but it is still being polished for stability, API clarity, and production readiness.

It is not recommended for real-world use yet. At this stage, the repository is mainly useful for following the design, experimenting locally, and discussing the model.
