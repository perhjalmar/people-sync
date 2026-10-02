# Quick Start Guide

## Prerequisites

- .NET 8 SDK or later
- A running instance of System B API (or use the included test double for local testing)

## Running the application

### Step 1: Build the solution

```bash
cd people-sync
dotnet build
```

### Step 2: Run against the test double (local testing)

Start the fake System B in one terminal:

```bash
dotnet run --project tests/PeopleSync.TestDouble -- --port 5000
```

In another terminal, run the sync:

```bash
dotnet run --project src/PeopleSync -- \
  --input examples/sample-input-1000.txt \
  --api http://localhost:5000 \
  --checkpoint checkpoint.log \
  --errors errors.log \
  --output people-output.xml
```

The records accepted by System B are also written to a timestamped file, e.g. `people-output_20261002T101530Z.xml` (UTC; `--output out/people.xml` gives `out/people_<timestamp>.xml`). The path is printed at the start of the run.

**Expected output:**
```
Done. Parsed=1000 Sent=2 Skipped=0 SentBatches=2
```

(1000 people ÷ 500 per batch = 2 batches)

### Step 3: Run against production System B

```bash
dotnet run --project src/PeopleSync -- \
  --input /data/persons.txt \
  --api https://system-b.example.com \
  --checkpoint /data/checkpoint.log \
  --errors /data/errors.log
```

### Step 4: Resume after a crash

Simply rerun the exact same command. The tool will:
1. Read the checkpoint log
2. Skip any batches already confirmed as sent
3. Resume from where it left off

Note: batches already in the checkpoint are not re-sent and not written again, so the output file of a resumed run only contains the newly sent batches. Each run creates a new timestamped file.

**No manual cleanup needed** — System B's `Idempotency-Key` header deduplication ensures no duplicates even if a batch was in-flight when the crash happened.

## Checking results

- **Success**: look for `Parsed=X Sent=Y` in the output
- **Errors**: check `errors.log` (JSON lines format) for bad records and rejected batches
- **Checkpoints**: check `checkpoint.log` to see which batches completed successfully

Example checkpoint line:
```
4f8f7c2a3b1d9e6a5c8d2f4b7e9a1c3d5f8a2b4c|1|sha256hash|2026-10-02T10:30:45.1234567+00:00
```

Format: `fingerprint|batchIndex|idempotencyKey|timestamp`

## Tuning

- **Batch size**: Edit `SyncRunner.cs`, line with `capacity: 500` (max 500 per API limit)
- **Rate limit**: Edit `PeopleApiClient` constructor; currently 10 req/s (per API limit)
- **Timeout**: Edit `PeopleApiClient` constructor; currently 60s (to handle 30s+ hangs mentioned in ops notes)

## Testing

Run the full test suite:

```bash
dotnet test people-sync.sln
```

Tests cover:
- Parser correctness (Victoria Bernadotte example, malformed lines, orphans)
- XML output format
- API client rate limiting (≤10 req/s) and retry logic
- End-to-end resume after simulated crash
- Memory usage on synthetic 1M-person file

## Monitoring

Check these files during or after a run:

1. **errors.log** — JSON lines, one entry per bad record or batch failure
2. **checkpoint.log** — Tab-separated confirmed batches; last line = resumption point
3. **Process exit code** — 0 = success, non-zero = crash/error

For a 3 AM incident, see [OPERATIONS.md](../OPERATIONS.md) in the root.
