# Index Data Model

This document defines what the in-memory index stores and the terms used. It does not cover when or how the index changes — see [Index Reconciliation Rulebook](./index-reconciliation-rulebook.md) for that.

## Source of Truth

Files on disk are the source of truth. The in-memory index is a lagging, rebuildable representation of the database records identified in those files; it is not a general-purpose inventory of every path on disk.

## FileIndexState

Indexed state of one path:

```text
FileIndexState(RecordIndexState, Status, ErrorInfo?, Projection?, Fingerprint, SchemaVersion?)
```

| Field | Meaning |
| --- | --- |
| `RecordIndexState` | Record state this path currently belongs to; it provides the id and the other paths associated with it. |
| `Status` | `Committed` for indexed file state or `Reserved` for a file name claimed by an API write. |
| `ErrorInfo?` | The last observed file error; absent for a reservation or successfully read file. |
| `Projection?` | Decoded record data from the last successful read; absent while a file name is only reserved. |
| `Fingerprint` | For `Committed`, the latest observed file metadata. For `Reserved`, the default value because no file has been observed yet. |
| `SchemaVersion?` | On-disk schema version from the last successful read; absent when versioning is not used or while the file is only reserved. |

The nullable fields are governed by these state invariants:

| State | `ErrorInfo` | `Projection` | `SchemaVersion` |
| --- | --- | --- | --- |
| `Reserved` | absent | absent | absent |
| `Committed`, successfully read, versioned | absent | present | present |
| `Committed`, successfully read, unversioned | absent | present | absent |
| `Committed`, read error | present | keeps the last known-good value | keeps the last known-good value, including absence for an unversioned file |

A path whose id has never been observed is not added to the index. `FileIndexState` represents a database file, not an arbitrary file-system path, and must belong to a `RecordIndexState` so that the index can maintain the `id <-> path` relation, apply the id-lock discipline, and perform winner selection. A file with no known id cannot participate in any of those operations. It remains part of the disk state and an input to reconciliation; after a later successful read reveals its id, `UpsertRecord` adds it to the index.

The following mutations update path state:

| Mutation | Effect |
| --- | --- |
| `UpsertRecord` | Sets `Status` to `Committed`; sets `Projection`, `Fingerprint`, and `SchemaVersion` from a successful read; clears `ErrorInfo`. |
| `UpsertError` | Sets `Status` to `Committed`; sets `Fingerprint` from the failed observation and sets `ErrorInfo`; `Projection` and `SchemaVersion` keep their last known-good values. |
| `ReserveFileName` | Adds a `Reserved` state associated with a known record before an API file write. |
| `CommitReservedFileName` | Converts a reservation into committed record state after a successful write. |

`Projection` and `ErrorInfo` are only used as state fields in this document; their internal representation is not specified here.

## RecordIndexState

All paths that share the same id are grouped into one `RecordIndexState`. A record can have multiple files on disk at once — for example during a rename or a conflicting write — but the logical database exposes exactly one file per id: the one selected by `CurrentFileName`.

## CurrentFileName

`CurrentFileName` selects, among all committed disk files that share an id, the one path considered authoritative. Reservations do not participate in logical last-write-wins (LWW) selection. Error state does not change a file's LWW rank: an invalid or unavailable winner remains the winner until the disk state changes.

```text
winner = max(LastWriteTimeUtc), then min(path)
```

Every other file with the same id is dormant. A dormant file must not be updated: writing to it changes its `LastWriteTimeUtc` and could make it the new `CurrentFileName` instead of the intended one.

## Fingerprint

A fingerprint is a cheap file-metadata snapshot — file size and `LastWriteTimeUtc` — used to detect whether a file may have changed without reading its contents. `GetFingerprint` reads only this metadata, so it is expected to be much faster than reading and decoding the file.

FolderDB relies on the file system and runtime reliably exposing size and `LastWriteTimeUtc` changes for every kind of edit it needs to observe. An environment that cannot guarantee this is not a good fit for FolderDB.

## SchemaVersion and CurrentSchemaVersion

Schema versioning is optional for a collection. When versioning is enabled, `SchemaVersion` is the version number of a file's on-disk schema. The schema defines how data is represented in the file, for example:

```text
UserV1(int Id, string FullName, SchemaVersion = 1)
UserV2(string Id, string FirstName, string LastName, SchemaVersion = 2)
```

`CurrentSchemaVersion` is the schema version the application currently uses. Because `SchemaVersion` is indexed, the engine can find files that need a format upgrade without reading them.

When versioning is not used, neither files nor decoded records have a schema version. `SchemaVersion` and `CurrentSchemaVersion` are both absent, and reconciliation does not run the file format update decisions.

When reading and decoding succeeds, `ReadFile` returns a record converted to `CurrentSchemaVersion`, together with the file's original on-disk `SchemaVersion`. A missing, unavailable, or invalid file produces the corresponding non-record result instead. Conversion happens in memory only — it does not by itself change the file on disk.
