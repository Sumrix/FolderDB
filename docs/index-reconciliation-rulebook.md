# Index Reconciliation Rulebook

This document describes the decision process for reconciling one file with the in-memory index.

## Chapter 1. Index Model

### The General Model

Files on disk are the source of truth; the index is a lagging, rebuildable representation of their observed state. A user or another process may create, edit, rename, or delete a file directly on disk without going through the library.

```text
files on disk -> reconciliation algorithm -> in-memory index
```

The reconciliation algorithm observes one path and performs two tasks: it reconciles the file with its indexed state (Chapters 1-3), and it keeps the file's on-disk schema up to date (Chapter 4).

### The Index

What the index stores (`FileIndexState`, `RecordIndexState`, and `CurrentFileName`) and how fingerprint and schema version are defined is described in [Index Data Model](./index-data-model.md). This chapter and the ones that follow only cover how reconciliation decides to change that state.

### The Reconciliation Algorithm

At the highest level, reconciliation has three steps:

```text
collect data -> analyze data -> update the index and file
```

#### Collect Data

The algorithm collects observations through three operations:

| Operation | Result | Meaning |
| --- | --- | --- |
| `GetFingerprint` | `{PresentFile, Fingerprint}` | Observes whether the file exists and, when it exists, returns its current fingerprint. |
| `GetState` | `{PresentState=1, Fingerprint, SchemaVersion?, Id, CurrentFileName, Error?}` \| `{PresentState=0}` | Reads the indexed state currently associated with the path. |
| `ReadFile` | `{PresentFile=1, Fingerprint, SchemaVersion?, Id, Record}` \| `{PresentFile=1, Fingerprint, Error}` \| `{PresentFile=0, Fingerprint}` | Reads and decodes the file, producing its complete observed state. |

Fingerprint is defined in [Index Data Model](./index-data-model.md#fingerprint). `GetFingerprint` does not read file contents, so it is expected to be much faster than `ReadFile`.

Each result is an observation made at a particular moment. The algorithm must assume that both the file system and the index may change between observations.

#### Analyze Data

The algorithm compares the observed file and indexed state using these inputs:

| Input | Question |
| --- | --- |
| File presence | Does the file currently exist? |
| State presence | Does the path currently have indexed state? |
| File kind | Is the observed file a record or an error? |
| State kind | Is the indexed state record-like or an error? |
| Id relation | Do the observed and indexed ids match? |
| Fingerprint relation | Do the observed and indexed fingerprints match? |
| Schema relation | Do the observed and indexed schema versions match? |
| Error relation | Do the observed and indexed errors match? |

From the available inputs, the index reconciliation algorithm produces one of these decisions. A decision mutates at most two ids, so it is described as two parts: the `indexedId` part mutates the id the path is currently attached to in the index, and the `diskId` part mutates the id read from the file.

| Decision | indexedId part | diskId part | Meaning |
| --- | --- | --- | --- |
| `Skip` | — | — | The observed file and indexed state are already consistent, or there is nothing that can be indexed. |
| `ReadFile` | — | — | More data is required; read the file and continue analysis. |
| `Delete` | `Delete` | — | Remove the path from its indexed id. |
| `UpsertRecord` | — | `UpsertRecord` | Add or update readable record state. |
| `UpsertError` | `UpsertError` | — | Add or update error state for a path whose indexed id is known. |
| `Delete`, then `UpsertRecord` | `Delete` | `UpsertRecord` | Move the path from the indexed id to the id read from the file. |

An empty part changes nothing. `ReadFile` is not a terminal decision, so it has no parts at all: it continues the analysis instead of changing the index. `UpsertError` belongs to the `indexedId` part because an error file has no readable id, so error state can only be written to an already known indexed id.

The file update algorithm produces one of these decisions:

| Decision | Meaning |
| --- | --- |
| `DoNothing` | Do not change the file. |
| `UpdateFile` | Atomically write the decoded record in the current format and obtain a new fingerprint. |

#### Update the Index

`Skip` completes index reconciliation without changing the index. Every other decision changes it, and each of its parts must be executed while holding the lock on the id that part mutates. [The Lock Boundary](#the-lock-boundary) explains why those locks are needed and how they are acquired, and [Chapter 3](#chapter-3-retry-decision) explains what happens when the held locks turn out not to cover every part. The executable algorithm is defined in [Chapter 5. Algorithm Implementation](#chapter-5-algorithm-implementation).

### Why the Decision Model Is Exhaustive

The index determines what records the database exposes to its users. A wrong decision can hide a valid record, retain a deleted record, attach a file to the wrong id, or replace useful state with an error. User data is the most important part of the database, so the decision process must explicitly cover every relevant situation.

The direct approach would be one truth collection with every combination of every input. With `N` independent binary inputs, that collection has `2^N` rows and `N` input columns. For the eight modeled inputs in this rulebook, that starts with `2^8 = 256` rows. Such a collection is difficult to review and mostly consists of duplicate or impossible situations. For example, the relation between file and indexed ids is unavailable when either side has no id.

Instead, the rulebook uses a series of connected collections. Each collection fixes some inputs, disables inputs that are no longer meaningful, and delegates the remaining cases to a smaller collection. These collections form a decision tree. The tree is shorter than the full truth collection while still making the complete reachable decision space reviewable.

### The Read Boundary

`ReadFile` is expensive because it performs file I/O and parses the contents. Many situations can be resolved using only `GetFingerprint` and `GetState`, so the decision chain is split by the read boundary:

```text
pre-read decision tree -> ReadFile, only when required -> post-read decision tree
```

The most important hot path is an existing readable indexed file whose fingerprint has not changed and which does not require a file format update. Reading file attributes for `GetFingerprint` is nearly instantaneous compared with reading and decoding the file. In that case, the pre-read trees return `Skip` and `DoNothing`, avoiding `ReadFile` entirely.

The two trees are different because they operate on different available data. Before `ReadFile`, the file id, file error, and complete post-read fingerprint are not available. After `ReadFile`, the algorithm can compare the complete observed file state with the index.

### The Lock Boundary

*This section derives the two-pass locking shape in prose and sketch pseudocode. [Chapter 5](#chapter-5-algorithm-implementation) gives the exact functions this sketch previews.*

In a concurrent environment, every index mutation and file format update must happen under id locks that cover the affected records. Both operations must be based on observations confirmed while those records are locked. Otherwise, one reconciliation worker could act on an older file state and overwrite a newer index or file state observed by another worker.

The strict form of the algorithm would therefore collect data and execute the decision while holding the required id locks:

```text
Lock()
fingerprint = GetFingerprint()
state = GetState()
decisions = MakePreReadDecisions(fingerprint, state)
if either decision requires ReadFile:
    readResult = ReadFile(path)
    decisions = MakePostReadDecisions(readResult, state)
ExecuteDecisions(decisions)
```

However, the complete lock set is not always known before the file is read. The indexed state may belong to one id, while the file on disk may contain another id. A new file may have no indexed id at all, and deciding whether an outdated file is the current file also requires its decoded id. If the algorithm locks only the ids known before the read, the decisions may still discover a different or additional id that must be locked. A strict locked implementation would then need a second pass with the correct locks:

```text
Lock()
fingerprint = GetFingerprint()
state = GetState()
decisions = MakePreReadDecisions(fingerprint, state)
if either decision requires ReadFile:
    readResult = ReadFile(path)
    decisions = MakePostReadDecisions(readResult, state)

Lock()
fingerprint = GetFingerprint()
state = GetState()
decisions = MakePreReadDecisions(fingerprint, state)
if either decision requires ReadFile:
    readResult = ReadFile(path)
    decisions = MakePostReadDecisions(readResult, state)
ExecuteDecisions(decisions)
```

This first lock rarely gives a useful benefit. Most unchanged files are resolved by `Skip` and `DoNothing` before `ReadFile`, and neither result requires locked work. When a file does need to be read, the read result can be treated as speculative and checked again after the required id locks are acquired. If the fingerprint observed under the locks still matches the cached read result, the read result is still valid for the locked decisions. If the fingerprint changed, the file is read again.

This avoids holding id locks during file I/O and decoding. It also keeps the hot path for already-current files lock-free. The first pass is used only to discover whether the index or file requires locked work and which ids it may affect. Decisions from the first pass are never executed directly.

The final shape of the algorithm is:

```text
without id locks:
    fingerprint = GetFingerprint()
    state = GetState()
    decisions = MakePreReadDecisions(fingerprint, state)
    if either decision requires ReadFile:
        readResult = ReadFile(path)
        decisions = MakePostReadDecisions(readResult, state)
    Skip + DoNothing -> return
    otherwise -> acquire id locks required id locks

with required id locks:
    fingerprint = GetFingerprint()
    state = GetState()
    decisions = MakePreReadDecisions(fingerprint, state)
    if either decision requires ReadFile:
        readResult = ReadFile(path)
        decisions = MakePostReadDecisions(readResult, state)
    ExecuteDecisions(decisions)
```

## Chapter 2. Decision Collections

This chapter derives the complete reconciliation decision tree. It formalizes which inputs are available in each situation, removes impossible combinations, and maps every reachable combination to a decision.

### Codes

Meanings:

| Code | Name | Values | Meaning |
| --- | --- | --- | --- |
| `PF` | Present File | `0`, `1` | The observed file exists on disk. |
| `PS` | Present State | `0`, `1` | The path has indexed state. |
| `EF` | Error File | `0`, `1` | The observed file is an error instead of a record. |
| `ES` | Error State | `0`, `1` | The indexed state is an error instead of record-like state. |
| `RI` | Relation Id | `same`, `different` | Relation between observed id and indexed id. |
| `RF` | Relation Fingerprint | `same`, `different` | Relation between observed fingerprint and indexed fingerprint. |
| `RS` | Schema Relation | `same`, `different` | Relation between observed on-disk schema version and indexed schema version. |
| `RE` | Relation Error | `same`, `different` | Relation between observed error and indexed error. |

Sets:

```text
U = {PF, PS, EF, ES, RI, RF, RS, RE}
```

Notation:

- `0` = false.
- `1` = true.
- `{}` = empty set.
- `Disabled` = decision inputs unavailable to the current collection.
- `Disables` = decision inputs made unavailable by the current row values.
- `Available` = decision inputs available to the current row.
- In collection rows, `Available` is calculated after applying `Disables`.

Formula:

```text
Available = U - Fixed - Disabled - Disables
```

### Availability Rules

| Values | Disables |
| --- | --- |
| `PF=0` | `{EF, RI, RF, RS, RE}` |
| `PS=0` | `{ES, RI, RF, RS, RE}` |
| `EF=1` | `{RI, RS}` |
| `EF=0` | `{RE}` |
| `ES=0` | `{RE}` |

### Pre-Read Decision

The executable reconciliation algorithm starts with the pre-read decision. It may decide early when the decision does not require file contents.

Before `ReadFile`, the algorithm can only observe file presence and fingerprint through `GetFingerprint`, and indexed state through `GetState`. The observed file kind, observed file id, and observed on-disk schema version are not available yet, so `EF`, `RI`, and `RS` are disabled. Because `RE` compares observed and indexed errors, it is unavailable without the observed file error.

#### Collection 1.1. Presence

```text
Fixed = {}
Disabled = {EF, RI, RS, RE}
Available = {PF, PS, ES, RF}
```

| PF | PS | Disables | Available | Decision | Reason |
| --- | --- | --- | --- | --- | --- |
| 0 | 0 | `{ES, RF}` | `{}` | `Skip` | The file and indexed state are absent before read: current. |
| 0 | 1 | `{RF}` | `{ES}` | [Collection 1.2](#collection-12-missing-file) | |
| 1 | 0 | `{ES, RF}` | `{}` | `ReadFile`, then [Post-Read Decision](#post-read-decision) | The file is new to the index and must be read before it can be indexed. |
| 1 | 1 | `{}` | `{ES, RF}` | [Collection 1.3](#collection-13-existing-file) | |

#### Collection 1.2. Missing File

```text
Fixed = {PF=0, PS=1}
Disabled = {EF, RI, RF, RS, RE}
Available = {ES}
```

| ES | Disables | Available | Decision |
| --- | --- | --- | --- |
| 0 | `{}` | `{}` | `Delete` |
| 1 | `{}` | `{}` | `Delete` |

The file is absent on disk, so its indexed state must be deleted.

#### Collection 1.3. Existing File

```text
Fixed = {PF=1, PS=1}
Disabled = {EF, RI, RS, RE}
Available = {ES, RF}
```

| ES | RF | Disables | Available | Decision | Reason |
| --- | --- | --- | --- | --- | --- |
| 0 | same | `{}` | `{}` | `Skip` | The indexed state is record-like and the pre-read fingerprint matches, so the file contents do not need to be read. |
| 0 | different | `{}` | `{}` | `ReadFile`, then [Post-Read Decision](#post-read-decision) | The record-like indexed state may be stale. |
| 1 | same | `{}` | `{}` | `ReadFile`, then [Post-Read Decision](#post-read-decision) | A readable file with the same fingerprint still requires `UpsertRecord` to recover from indexed error state. |
| 1 | different | `{}` | `{}` | `ReadFile`, then [Post-Read Decision](#post-read-decision) | The indexed error state may be stale. |

#### Compact Pre-Read Decision Tree

```text
GetFingerprint -> PF, pre-read file fingerprint
GetState       -> PS, ES, indexed state
├─ PF=0, PS=0 -> Skip
├─ PF=0, PS=1 -> Delete
├─ PF=1, PS=0 -> ReadFile -> Post-Read Decision
└─ PF=1, PS=1
   ├─ ES=0, RF=same -> Skip
   └─ otherwise     -> ReadFile -> Post-Read Decision
```

### Post-Read Decision

The post-read decision starts when the pre-read decision returns `ReadFile`. It reads the file, builds post-read inputs, and delegates to the complete reconciliation collections below.

Because `ReadFile` produces a new `{PresentFile, Fingerprint, SchemaVersion?}` observation, the post-read values of `PF` and `RF` are different observations from the values used in the pre-read decision.

#### Collection 2.1. Presence

```text
Fixed = {}
Disabled = {}
Available = {PF, PS, EF, ES, RI, RF, RS, RE}
```

| PF | PS | Disables | Available | Decision | Reason |
| --- | --- | --- | --- | --- | --- |
| 0 | 0 | `{EF, ES, RI, RF, RS, RE}` | `{}` | `Skip` | The file and indexed state are absent: current. |
| 0 | 1 | `{EF, RI, RF, RS, RE}` | `{ES}` | [Collection 2.2](#collection-22-missing-file) | |
| 1 | 0 | `{ES, RI, RF, RS, RE}` | `{EF}` | [Collection 2.3](#collection-23-new-file) | |
| 1 | 1 | `{}` | `{EF, ES, RI, RF, RS, RE}` | [Collection 2.4](#collection-24-content-kind) | |

#### Collection 2.2. Missing File

```text
Fixed = {PF=0, PS=1}
Disabled = {EF, RI, RF, RS, RE}
Available = {ES}
```

| ES | Disables | Available | Decision |
| --- | --- | --- | --- |
| 0 | `{}` | `{}` | `Delete` |
| 1 | `{}` | `{}` | `Delete` |

The file is absent on disk, so its indexed state must be deleted.

#### Collection 2.3. New File

```text
Fixed = {PF=1, PS=0}
Disabled = {ES, RI, RF, RS, RE}
Available = {EF}
```

| EF | Disables | Available | Decision | Reason |
| --- | --- | --- | --- | --- |
| 0 | `{}` | `{}` | `UpsertRecord` | The file is readable and absent from the index: add it. |
| 1 | `{}` | `{}` | `Skip` | The file id is unknown, so no `RecordIndexState`, id lock, or `id <-> path` relation can be established. Keep the path as disk state only; a later successful read can add it to the index. |

#### Collection 2.4. Content Kind

```text
Fixed = {PF=1, PS=1}
Disabled = {}
Available = {EF, ES, RI, RF, RS, RE}
```

| EF | ES | Disables | Available | Decision | Reason |
| --- | --- | --- | --- | --- | --- |
| 0 | 0 | `{RE}` | `{RI, RF, RS}` | [Collection 2.5](#collection-25-record-state-comparison) | |
| 1 | 0 | `{RI, RS, RE}` | `{RF}` | `UpsertError` | The indexed record state differs from the file: reconcile it as error state. |
| 0 | 1 | `{RE}` | `{RI, RF, RS}` | [Collection 2.6](#collection-26-record-recovery-from-error) | |
| 1 | 1 | `{RI, RS}` | `{RF, RE}` | [Collection 2.7](#collection-27-error-comparison) | |

#### Collection 2.5. Record State Comparison

```text
Fixed = {PF=1, PS=1, EF=0, ES=0}
Disabled = {RE}
Available = {RI, RF, RS}
```

| RI | RF | RS | Disables | Available | Decision | Reason |
| --- | --- | --- | --- | --- | --- | --- |
| same | same | same | `{}` | `{}` | `Skip` | The indexed record matches the file: current. |
| same | same | different | `{}` | `{}` | `UpsertRecord` | The indexed schema version must be actual. |
| same | different | same | `{}` | `{}` | `UpsertRecord` | The indexed record must be actual. |
| same | different | different | `{}` | `{}` | `UpsertRecord` | The indexed state must be actual. |
| different | same | same | `{}` | `{}` | `Delete`, then `UpsertRecord` | The indexed id must be actual. |
| different | same | different | `{}` | `{}` | `Delete`, then `UpsertRecord` | The indexed id must be actual. |
| different | different | same | `{}` | `{}` | `Delete`, then `UpsertRecord` | The indexed id must be actual. |
| different | different | different | `{}` | `{}` | `Delete`, then `UpsertRecord` | The indexed id must be actual. |

#### Collection 2.6. Record Recovery From Error

```text
Fixed = {PF=1, PS=1, EF=0, ES=1}
Disabled = {RE}
Available = {RI, RF, RS}
```

| RI | RF | RS | Disables | Available | Decision |
| --- | --- | --- | --- | --- | --- |
| same | same | same | `{}` | `{}` | `UpsertRecord` |
| same | same | different | `{}` | `{}` | `UpsertRecord` |
| same | different | same | `{}` | `{}` | `UpsertRecord` |
| same | different | different | `{}` | `{}` | `UpsertRecord` |
| different | same | same | `{}` | `{}` | `Delete`, then `UpsertRecord` |
| different | same | different | `{}` | `{}` | `Delete`, then `UpsertRecord` |
| different | different | same | `{}` | `{}` | `Delete`, then `UpsertRecord` |
| different | different | different | `{}` | `{}` | `Delete`, then `UpsertRecord` |

The indexed error state can be recovered.

#### Collection 2.7. Error Comparison

```text
Fixed = {PF=1, PS=1, EF=1, ES=1}
Disabled = {RI, RS}
Available = {RF, RE}
```

| RF | RE | Disables | Available | Decision | Reason |
| --- | --- | --- | --- | --- | --- |
| same | same | `{}` | `{}` | `Skip` | The indexed error matches the file: current. |
| same | different | `{}` | `{}` | `UpsertError` | The indexed error differs from the file: reconcile it. |
| different | same | `{}` | `{}` | `UpsertError` | The indexed fingerprint differs from the file: reconcile it. |
| different | different | `{}` | `{}` | `UpsertError` | The indexed error and fingerprint differ from the file: reconcile them. |

#### Compact Post-Read Decision Tree

```text
ReadFile -> PF, file fingerprint, file id, file error
Use current GetState result -> PS, ES, indexed state
├─ PF=0, PS=0 -> Skip
├─ PF=0, PS=1 -> Delete
├─ PF=1, PS=0
│  ├─ EF=0 -> UpsertRecord
│  └─ EF=1 -> Skip
└─ PF=1, PS=1
   ├─ EF=0, ES=0
   │  ├─ RI=same, RF=same, RS=same -> Skip
   │  ├─ RI=same, otherwise        -> UpsertRecord
   │  └─ RI=different              -> Delete, then UpsertRecord
   ├─ EF=1, ES=0 -> UpsertError
   ├─ EF=0, ES=1
   │  ├─ RI=same      -> UpsertRecord
   │  └─ RI=different -> Delete, then UpsertRecord
   └─ EF=1, ES=1
      ├─ RF=same, RE=same -> Skip
      └─ otherwise        -> UpsertError
```

## Chapter 3. Retry Decision

File reconciliation produces two independent decisions. The reconciliation decision from Chapter 2 determines whether and how the index should change. The retry decision determines whether the complete reconciliation operation should run again and, if so, which delay policy the scheduler should use.

A retry does not imply that the reconciliation decision failed. For example, a transient read error can be written to an already known indexed id while still requesting another reconciliation attempt because the error may disappear. Conversely, a persistent read error can be represented in the index without requesting another attempt.

Reconciliation can also discover that the held id locks do not cover the ids required by the current decision. This is where the two parts of a decision stop being an abstraction: the `indexedId` part may be covered while the `diskId` part is not. The covered part executes, and the remaining part cannot execute safely. Only now can that leftover be accounted for, because the required ids are already known and a retry can acquire the correct locks and finish the decision from the current disk state.

The order matters and is not symmetric. The `indexedId` part detaches the path from its indexed id, which is always safe to do on its own. The `diskId` part attaches the path to the id read from the file, which is not safe while the path is still attached to another id. A decision may therefore stop after its `indexedId` part, but never after its `diskId` part alone.

This chapter defines only the retry decision produced by reconciliation. It does not define the scheduler implementation. A scheduler may use any queue and backoff algorithm that preserves the following contract:

| Decision | Meaning |
| --- | --- |
| `Complete` | Do not schedule another reconciliation attempt. |
| `RetryWithBackoff` | Schedule another attempt using the continuing backoff progression. |
| `RetryWithMinBackoff` | Schedule another attempt using the minimum delay and restart the backoff progression. |

The retry decision is evaluated before every normal return from full or partial reconciliation. It uses the latest file read result available on that return path and whether an id lock mismatch prevented the reconciliation decision from being executed.

As in Chapter 2, the complete input space is defined by a small set of values:

| Code | Name | Values | Meaning |
| --- | --- | --- | --- |
| `EF` | Error File | `0`, `1` | Did the latest file read produce an error? |
| `TE` | Transient Error | `0`, `1` | Is the file read error transient? |
| `IM` | Id Lock Mismatch | `0`, `1` | Do the held id locks fail to cover the ids required by the reconciliation decision? |

Sets:

```text
R = {EF, TE, IM}
```

The retry decision does not depend on which reconciliation decision was selected. It depends only on the read error and whether the selected decision can be executed under the held id locks.

### Availability Rules

| Values | Disables |
| --- | --- |
| `EF=0` | `{TE}` |

#### Collection 3.1. Retry Conditions

```text
Fixed = {}
Disabled = {}
Available = {EF, TE, IM}
```

| EF | IM | Disables | Available | Decision | Reason |
| --- | --- | --- | --- | --- | --- |
| 0 | 0 | `{TE}` | `{}` | `Complete` | No retry condition was observed. |
| 0 | 1 | `{TE}` | `{}` | `RetryWithMinBackoff` | The decision cannot execute under the held locks, but the required ids are now known. Retry with the minimum delay to acquire the correct locks. |
| 1 | 0 | `{}` | `{TE}` | [Collection 3.2](#collection-32-read-error-persistence) | |
| 1 | 1 | `{}` | `{TE}` | [Collection 3.3](#collection-33-id-lock-mismatch-precedence) | |

#### Collection 3.2. Read Error Persistence

```text
Fixed = {EF=1, IM=0}
Disabled = {}
Available = {TE}
```

| TE | Disables | Available | Decision | Reason |
| --- | --- | --- | --- | --- |
| 0 | `{}` | `{}` | `Complete` | Automatically repeating reconciliation is not expected to resolve a persistent error. |
| 1 | `{}` | `{}` | `RetryWithBackoff` | A transient error may disappear, so retry while continuing the backoff progression. |


#### Collection 3.3. Id Lock Mismatch Precedence

```text
Fixed = {EF=1, IM=1}
Disabled = {}
Available = {TE}
```

| TE | Disables | Available | Decision | Reason |
| --- | --- | --- | --- | --- |
| 0 | `{}` | `{}` | `RetryWithMinBackoff` | The error could not be written to the index under the held locks. The required ids are now known, so retry with the minimum delay. |
| 1 | `{}` | `{}` | `RetryWithMinBackoff` | The id lock mismatch takes priority for this attempt. Retry with the minimum delay using the now-known ids. If the transient error persists after the lock mismatch is resolved, a later attempt will continue with regular backoff. |

#### Compact Retry Decision Tree

```text
Retry inputs -> EF, TE, IM
├─ IM=1             -> RetryWithMinBackoff
├─ EF=1, TE=1       -> RetryWithBackoff
└─ otherwise         -> Complete
```

The id lock mismatch branch comes first because it prevented the current reconciliation decision from executing and the required lock set is already known. Once that mismatch is resolved, a transient read error that remains follows the regular backoff branch on subsequent attempts.

## Chapter 4. File Format Update

This chapter applies only when schema versioning is enabled and `CurrentSchemaVersion` has a value. When versioning is not used, `CurrentSchemaVersion` is absent and reconciliation skips the entire file format update flow: it does not calculate `FileUpdateIntent` or `FileUpdateDecision` and performs only index reconciliation.

This chapter uses `SchemaVersion`, `CurrentSchemaVersion`, `CurrentFileName`, and `Fingerprint` as defined in [Index Data Model](./index-data-model.md). `ReadFile` converts a record to `CurrentSchemaVersion` and returns it alongside the file's original on-disk `SchemaVersion`; this conversion happens in memory and does not itself change the file on disk. Updating the file requires writing the record back to disk and then writing the resulting new fingerprint to the index.

The file update is part of full reconciliation: it uses an already obtained `ReadResult`, runs under the same id locks, and changes the observed file state, which must then be reflected in the index. A user operation that reads a file on its own does not trigger this update — it may return the converted record without waiting for an additional disk write. The update happens later, in a background reconciliation pass.

`FileUpdateIntent` is determined alongside `IndexDecision` during the Pre-Read and Post-Read Decisions. The final `FileUpdateDecision` is made after the `IndexDecision` from [Chapter 2](#chapter-2-decision-collections) has been applied to the index. This ordering lets the index first establish the up-to-date path-to-id relation and recalculate `CurrentFileName`. Only then can reconciliation reliably determine whether the processed file is the one selected by `CurrentFileName`.

```text
make Pre-Read IndexDecision and FileUpdateIntent
    -> read and decode file when either decision requires it
    -> make Post-Read IndexDecision and FileUpdateIntent
    -> execute IndexDecision
    -> recalculate CurrentFileName
    -> make FileUpdateDecision from FileUpdateIntent and CF
    -> execute UpdateFile when selected
    -> reconcile the write result into index
```

### Inputs

The file update trees use the inputs, availability rules, and observation boundaries from the Pre-Read and Post-Read Decisions in [Chapter 2](#chapter-2-decision-collections). Two new inputs are added to the general set `U`:

| Code | Name | Values | Meaning |
| --- | --- | --- | --- |
| `SA` | Schema Actual | `0`, `1` | Whether the physical file has the current schema version. |
| `CF` | Current File | `0`, `1` | Whether the processed file name matches the record's `CurrentFileName`. |

```text
M = U ∪ {SA, CF}
  = {PF, PS, EF, ES, RI, RF, RS, RE, SA, CF}
```

### Availability Rules

The availability rules are the rules from [Chapter 2](#chapter-2-decision-collections) together with the following additions.

During the Pre-Read Decision, `SA` and `CF` come from the indexed state. They describe the observed file only when the index contains a record-like state with the same fingerprint:

| Values | Disables |
| --- | --- |
| `PS=0` | `{SA, CF}` |
| `PF=0` | `{SA, CF}` |
| `ES=1` | `{SA, CF}` |
| `RF=different` | `{SA, CF}` |

During the Post-Read Decision, `SA` comes from a successful `ReadResult`:

| Values | Disables |
| --- | --- |
| `PF=0` | `{SA}` |
| `EF=1` | `{SA}` |

The Post-Read Decision does not use `CF` immediately after reading the file because `CF` is calculated again from the updated index after executing `IndexDecision`. That new value is then used to determine the final file update decision.

Therefore, the pre-read `CF` and the `CF` observed after executing `IndexDecision` are different observations. The first avoids reading a dormant file when the index already describes its state reliably. The second determines whether the file that was read may be updated.

### Decisions

The file update decision collections used by the Pre-Read and Post-Read Decisions return a `FileUpdateIntent`:

| Intent | Meaning |
| --- | --- |
| `DoNothing` | No additional file format update action is required. |
| `ReadFile` | Read the file to obtain the data required to decide whether its format should be updated. |
| `UpdateIfCurrentFile` | After executing `IndexDecision`, update the file format if this is the record's current file. |

After executing `IndexDecision`, `UpdateIfCurrentFile` is resolved into the final `FileUpdateDecision` described in [File Update Decision](#file-update-decision).

### Pre-Read File Update Intent

The pre-read tree determines whether the file must be read to update its format. If the index reliably shows that the file is dormant, the tree completes without reading it. Whenever it is unknown whether the schema is current or whether the file represents the current record, the tree requires a read.

#### Collection 4.1. Presence

```text
Fixed = {}
Disabled = {EF, RI, RS, RE}
Available = {PF, PS, ES, RF, SA, CF}
```

| PF | PS | Disables | Available | FileUpdateIntent | Reason |
| --- | --- | --- | --- | --- | --- |
| 0 | 0 | `{ES, RF, SA, CF}` | `{}` | `DoNothing` | There is no file to update. |
| 0 | 1 | `{RF, SA, CF}` | `{ES}` | [Collection 4.2](#collection-42-missing-file) | |
| 1 | 0 | `{ES, RF, SA, CF}` | `{}` | `ReadFile`, then [Post-Read Decision](#post-read-file-update-intent) | The file must be read to determine its id, schema version, and relation to the current record. |
| 1 | 1 | `{}` | `{ES, RF, SA, CF}` | [Collection 4.3](#collection-43-existing-file) | |

#### Collection 4.2. Missing File

```text
Fixed = {PF=0, PS=1}
Disabled = {EF, RI, RF, RS, RE, SA, CF}
Available = {ES}
```

| ES | Disables | Available | FileUpdateIntent | Reason |
| --- | --- | --- | --- | --- |
| 0 | `{}` | `{}` | `DoNothing` | There is no file to update. |
| 1 | `{}` | `{}` | `DoNothing` | There is no file to update. |

#### Collection 4.3. Existing File

```text
Fixed = {PF=1, PS=1}
Disabled = {EF, RI, RS, RE}
Available = {ES, RF, SA, CF}
```

| ES | RF | Disables | Available | FileUpdateIntent | Reason |
| --- | --- | --- | --- | --- | --- |
| 0 | same | `{}` | `{SA, CF}` | [Collection 4.4](#collection-44-actual-indexed-state) | |
| 0 | different | `{SA, CF}` | `{}` | `ReadFile`, then [Post-Read Decision](#post-read-file-update-intent) | The index no longer describes the observed file state. |
| 1 | same | `{SA, CF}` | `{}` | `ReadFile`, then [Post-Read Decision](#post-read-file-update-intent) | An error state does not confirm the schema version or the observed file's relation to the current record. |
| 1 | different | `{SA, CF}` | `{}` | `ReadFile`, then [Post-Read Decision](#post-read-file-update-intent) | An error state does not confirm the schema version or the observed file's relation to the current record. |

#### Collection 4.4. Actual Indexed State

```text
Fixed = {PF=1, PS=1, ES=0, RF=same}
Disabled = {EF, RI, RS, RE}
Available = {SA, CF}
```

| SA | CF | Disables | Available | FileUpdateIntent | Reason |
| --- | --- | --- | --- | --- | --- |
| 0 | 0 | `{}` | `{}` | `DoNothing` | Only a record's current file is updated. |
| 0 | 1 | `{}` | `{}` | `ReadFile`, then [Post-Read Decision](#post-read-file-update-intent) | The current file must be updated to the current schema. |
| 1 | 0 | `{}` | `{}` | `DoNothing` | The file already has the current schema version. |
| 1 | 1 | `{}` | `{}` | `DoNothing` | The file already has the current schema version. |


### Post-Read File Update Intent

The post-read tree runs after a read attempt and determines `FileUpdateIntent` again from the new observation. It requires only data from the read result itself:

```text
M_post = {PF, EF, SA}
```

`PS`, `ES`, `RI`, `RF`, `RS`, and `RE` are not inputs to this tree and do not affect `FileUpdateIntent`. `CF` is also unavailable at this stage and will be calculated after executing `IndexDecision`.

#### Collection 4.5. Presence

```text
Fixed = {}
Disabled = {}
Available = {PF, EF, SA}
```

| PF | Disables | Available | FileUpdateIntent | Reason |
| --- | --- | --- | --- | --- |
| 0 | `{EF, SA}` | `{}` | `DoNothing` | There is no file to update. |
| 1 | `{}` | `{EF, SA}` | [Collection 4.6](#collection-46-content-kind) | |

#### Collection 4.6. Content Kind

```text
Fixed = {PF=1}
Disabled = {}
Available = {EF, SA}
```

| EF | Disables | Available | FileUpdateIntent | Reason |
| --- | --- | --- | --- | --- |
| 0 | `{}` | `{SA}` | [Collection 4.7](#collection-47-schema-actual) | |
| 1 | `{SA}` | `{}` | `DoNothing` | Without a decoded record, the file cannot be written in the current format. |

#### Collection 4.7. Schema Actual

```text
Fixed = {PF=1, EF=0}
Disabled = {}
Available = {SA}
```

| SA | Disables | Available | FileUpdateIntent | Reason |
| --- | --- | --- | --- | --- |
| 0 | `{}` | `{}` | `UpdateIfCurrentFile` | The successfully decoded file has an outdated schema version. |
| 1 | `{}` | `{}` | `DoNothing` | The file already has the current schema version. |

`UpdateIfCurrentFile` does not yet authorize a write. After executing `IndexDecision`, the intent is completed with the new `CF` value.

### File Update Decision

After executing `IndexDecision`, the index recalculates `CurrentFileName`. `FileUpdateIntent` can then be resolved into a final decision.

```text
M_update = {FileUpdateIntent, CF}
```

#### Collection 4.8. Current File

```text
Fixed = {}
Disabled = {}
Available = {FileUpdateIntent, CF}
```

| FileUpdateIntent | CF | Disables | Available | FileUpdateDecision |
| --- | --- | --- | --- | --- |
| `DoNothing` | 0 | `{}` | `{}` | `DoNothing` |
| `DoNothing` | 1 | `{}` | `{}` | `DoNothing` |
| `UpdateIfCurrentFile` | 0 | `{}` | `{}` | `DoNothing` |
| `UpdateIfCurrentFile` | 1 | `{}` | `{}` | `UpdateFile` |

`FileUpdateIntent.ReadFile` cannot reach this collection because it is consumed by the transition to the Post-Read Decision. A dormant file is not updated even when it has an outdated schema version. A successful write of the current file returns a new fingerprint, which must then be reflected in the index.

### Compact Decision Trees

#### Pre-Read Decision

```text
Pre-read inputs -> PF, PS, ES, RF, SA, CF
├─ IndexDecision      -> Compact Pre-Read Decision Tree from Chapter 2
└─ FileUpdateIntent
   ├─ PF=0                                      -> DoNothing
   ├─ PF=1, PS=0                                -> ReadFile
   └─ PF=1, PS=1
      ├─ ES=0, RF=same
      │  ├─ SA=0, CF=1                         -> ReadFile
      │  └─ otherwise                          -> DoNothing
      └─ otherwise                             -> ReadFile

Go to Post-Read Decision when
IndexDecision=ReadFile OR FileUpdateIntent=ReadFile
```

#### Post-Read Decision

```text
Post-read observation -> PF, PS, EF, ES, RI, RF, RS, RE, SA
├─ IndexDecision inputs      -> PF, PS, EF, ES, RI, RF, RS, RE
│  └─ Compact Post-Read Decision Tree from Chapter 2
└─ FileUpdateIntent inputs   -> PF, EF, SA
   ├─ PF=0                 -> DoNothing
   └─ PF=1
      ├─ EF=1             -> DoNothing
      └─ EF=0
         ├─ SA=0          -> UpdateIfCurrentFile
         └─ SA=1          -> DoNothing
```

#### File Update Decision

```text
File update inputs -> FileUpdateIntent, CF
├─ FileUpdateIntent=DoNothing                 -> DoNothing
└─ FileUpdateIntent=UpdateIfCurrentFile
   ├─ CF=0                                    -> DoNothing
   └─ CF=1                                    -> UpdateFile
```

## Chapter 5. Algorithm Implementation

This chapter combines the index decisions from Chapter 2, the retry decisions from Chapter 3, and the file update decisions from Chapter 4 into one reconciliation algorithm.

The pseudocode below includes the Chapter 4 flow and assumes that schema versioning is enabled. A production implementation must also support collections without versioning. When `CurrentSchemaVersion` is absent, it must omit all Chapter 4 logic: `FileUpdateIntent` and `FileUpdateDecision` are not calculated, and reconciliation executes only the index and retry decisions. The pseudocode intentionally remains unchanged because it describes the complete versioned algorithm rather than every implementation optimization.

### Full Algorithm Scheme

Given the model and decision collections from the previous chapters, the algorithm pseudocode looks like this:

```text
RetryDecision Reconcile(path)

    # Pass 1: no id locks. Decisions are discovered here, never executed.
    pass1 = Decide(path, readCache: none)

    if pass1.indexDecision is Skip AND pass1.fileUpdateIntent is DoNothing:
        return MakeRetryDecision(pass1.lastReadResult)       # hot path: nothing to do
    else
        requiredIdLocks1 = GetRequiredIdLocks(
            pass1.indexDecision,
            pass1.fileUpdateIntent)
        acquiredLocks = Acquire(requiredIdLocks1)

        # Pass 2: the same two steps, now under the locks. Only these decisions may execute.
        pass2 = Decide(path, readCache: pass1.lastReadResult)

        if pass2.indexDecision is Skip AND pass2.fileUpdateIntent is DoNothing:
            return MakeRetryDecision(pass2.lastReadResult)
        else
            # Each part runs under the lock it needs, in the order given by the decision collection.
            # The index goes first: it recalculates CurrentFileName for the file update.
            if acquiredLocks.Cover(pass2.indexDecision.IndexedIdPart)
                Execute(pass2.indexDecision.IndexedIdPart, path)

                if acquiredLocks.Cover(pass2.indexDecision.DiskIdPart)
                    Execute(pass2.indexDecision.DiskIdPart, path)

                    switch pass2.fileUpdateIntent
                    case UpdateIfCurrentFile:
                        currentFile = IsCurrentFile(pass2.lastReadResult.Id, path)
                        fileUpdateDecision = MakeFileUpdateDecision(
                            pass2.fileUpdateIntent,
                            currentFile)

                        switch fileUpdateDecision
                        case UpdateFile:
                            writeResult = UpdateFile(path, pass2.lastReadResult.Record)
                            ReconcileWriteResult(writeResult)
                            return MakeRetryDecision(writeResult)

                        case DoNothing:
                            return MakeRetryDecision(pass2.lastReadResult)

                    case DoNothing:
                        return MakeRetryDecision(pass2.lastReadResult)

                else
                    # The diskId part is not covered: it waits for the next attempt.
                    return MakeRetryDecision(pass2.lastReadResult, idLockMismatch: true)
            else
                return MakeRetryDecision(pass2.lastReadResult, idLockMismatch: true)

DecisionPass Decide(path, readCache)
    fingerprint = GetFingerprint(path)
    state = GetState(path)

    decisions = MakePreReadDecisions(fingerprint, state)

    if decisions.indexDecision is ReadFile or
       decisions.fileUpdateIntent is ReadFile
        readResult = ResolveReadFile(path, readCache, fingerprint)
        decisions = MakePostReadDecisions(readResult, state)
        return DecisionPass(
            decisions.indexDecision,
            decisions.fileUpdateIntent,
            lastReadResult: readResult)
    else
        return DecisionPass(
            decisions.indexDecision,
            decisions.fileUpdateIntent,
            lastReadResult: readCache)

ReadResult ResolveReadFile(path, readCache, fingerprint)
    if readCache matches fingerprint
        return readCache
    else
        return ReadFile(path)
```

Here, `Reconcile` represents full reconciliation. The first pass discovers whether either the index or the file requires locked work and which ids that work may affect. The second pass confirms both decisions under those id locks. A pass is complete without locks only when its `IndexDecision` is `Skip` and its `FileUpdateIntent` is `DoNothing`.

`GetRequiredIdLocks` returns the ids that must be locked to execute `IndexDecision` and `FileUpdateIntent`. For `IndexDecision` these are the ids of its parts. `FileUpdateIntent` can require a lock of its own, because a file may only be changed while its id is locked.

The nesting of the two lock checks is what implements the asymmetry described in [Chapter 3](#chapter-3-retry-decision). The `diskId` part is unreachable without having executed the `indexedId` part first, so a decision can stop after its `indexedId` part but can never execute its `diskId` part alone. Lock mismatch therefore never rolls back already safe work: the covered part stays applied, and `RetryWithMinBackoff` carries the rest to the next attempt.

After the second pass, the algorithm first executes both parts of `IndexDecision`. This establishes the current path-to-id relation and recalculates `CurrentFileName`. Only then does it resolve `UpdateIfCurrentFile` into a final `FileUpdateDecision`. A successful `UpdateFile` produces a new fingerprint and current physical schema version; `ReconcileWriteResult` immediately reflects that new file state in the index while the same id lock is still held.

`Decide` implements one complete decision pass. It collects the pre-read inputs and produces both `IndexDecision` and `FileUpdateIntent`. It crosses the read boundary when either decision requires `ReadFile`, then recalculates both decisions from the post-read observation. It carries the latest read result so the second pass can reuse the same file observation when the fingerprint still matches and so the retry decision can inspect the latest read error. When the current pass does not read the file, the cached result from the previous pass remains the latest read result.

`ResolveReadFile` reads the file or returns the cached state.

`MakeRetryDecision` maps the available read error and id lock state through the collections from Chapter 3.

### Implementation Architecture

The implementation should map the rulebook concepts to a small set of classes. The class names below describe roles. They are not final production names.

```text
FileReconciler
├─ DecisionMaker
├─ DecisionExecutor
└─ RetryDecisionMaker
```

`FileReconciler` implements the full reconciliation process described in Chapter 5. It owns the scenario flow: collecting observations, coordinating locks, calling the decision maker, and asking the executor to apply confirmed index and file update decisions.

`FileReconciler` should expose two reconciliation entry points:

| Entry point | Used by | Meaning |
| --- | --- | --- |
| Full reconciliation | File system event processing | Runs index reconciliation and file format update. |
| Partial reconciliation | User command processing after reading a file | Reconciles the already-read result with the index under the lock already held by the caller. It does not update the file format. |

`DecisionMaker` owns the pure index and file update decision collections from Chapters 2 and 4. It has no file system access, no index mutation access, no locks, and no retry logic. Its responsibility is limited to mapping already collected observations to decisions:

```text
MakePreReadDecisions(fingerprintObservation, indexedStateObservation)
    -> {IndexDecision, FileUpdateIntent}

MakePostReadDecisions(readObservation, indexedStateObservation)
    -> {IndexDecision, FileUpdateIntent}

MakeFileUpdateDecision(fileUpdateIntent, currentFile)
    -> FileUpdateDecision
```

`DecisionExecutor` executes one part at a time under the id lock that part requires. It does not choose when to read a file, when to acquire locks, or when to retry. `FileReconciler` gives it the `indexedId` part, then the `diskId` part, then the `FileUpdateDecision` when selected and reconciles a successful write result into the index:

| Decision kind | Part or decision | Executor action |
| --- | --- | --- |
| `IndexDecision` | empty part | Do not change the index. |
| `IndexDecision` | `Delete` | Remove the path from the indexed id. |
| `IndexDecision` | `UpsertRecord` | Add or refresh readable record state on the id read from the file. |
| `IndexDecision` | `UpsertError` | Add or refresh error state on the indexed id. |
| `FileUpdateDecision` | `DoNothing` | Do not change the file. |
| `FileUpdateDecision` | `UpdateFile` | Atomically write the decoded record in the current format and reflect the successful write result in the index. |

`RetryDecisionMaker` owns the pure retry collections from Chapter 3. It has no file system access, no index mutation access, and no scheduling logic. Before each normal return, `FileReconciler` gives it the latest available read error and the id lock mismatch state. The resulting retry decision is returned to the scheduler.
