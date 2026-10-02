# People Sync

People Sync streams the nightly System A person export into System B's batch XML API without loading the full file into memory. The implementation targets the assignment constraints: Windows-1252 legacy input, a 500-person API batch limit, a 10 req/s API ceiling, crash-safe resume, and traceable bad data handling.

## Run

```bash
dotnet build /home/runner/work/people-sync/people-sync/people-sync.sln
dotnet test /home/runner/work/people-sync/people-sync/people-sync.sln
dotnet run --project /home/runner/work/people-sync/people-sync/src/PeopleSync/PeopleSync.csproj -- \
  --input /absolute/path/to/people.txt \
  --api http://localhost:5000/people/batch \
  --checkpoint /absolute/path/to/checkpoint.log \
  --errors /absolute/path/to/errors.jsonl \
  --output /absolute/path/to/people-output.xml
```

`--output` (default `people-output.xml` in the working directory) is a copy of the people records System B accepted. A UTC timestamp is inserted before the extension, e.g. `out/people.xml` becomes `out/people_20261002T101530Z.xml`; missing directories are created and the final path is printed at start. The file is one well-formed UTF-8 (no BOM) `<people>` document containing the `<person>` elements of all accepted batches, streamed batch by batch (flushed each time, closing tag kept in place so a crash still leaves valid XML). Batches skipped on resume are not rewritten; each run produces its own file.

For local verification, start the fake System B with:

```bash
dotnet run --project /home/runner/work/people-sync/people-sync/tests/PeopleSync.TestDouble/PeopleSync.TestDouble.csproj
```

## Assumptions and ambiguities

- Input encoding defaults to Windows-1252 and only switches to UTF-8 when a BOM is present; this fits the "old Swedish system" hint and keeps `å`, `ä`, and `ö` safe.
- `P` starts a new person, `D` is person-only, and `T`/`A` attach to the current family member when an `F` is open, otherwise to the current person.
- `T`, `A`, and `D` are treated as single-occurrence optional blocks. Later duplicates are logged as warnings and ignored so the first accepted state remains stable across reruns.
- Malformed or orphaned lines are logged to `errors.jsonl` as JSON lines and skipped instead of aborting the run.
- Idempotency is defined per file fingerprint plus batch index. If the file content changes, the fingerprint changes and the sync starts fresh.

## Trade-offs

- The checkpoint store is an append-only log instead of a mutable state file, which keeps crash recovery simple and auditable.
- Each batch is rendered to XML in-memory before send. That keeps the implementation straightforward while still respecting the 500-person/256 MB constraints.
- Retry logic is limited to 429, 503, and timeouts with exponential backoff. I did not add a full circuit breaker because the assignment only requires resilient retries.

## Verification strategy

The xUnit suite covers parser behavior, exact XML formatting, rate limiting, retries, idempotency headers, end-to-end fake server sync, crash-and-resume semantics, and a 1,000,000-person streaming memory check. The fake System B validates the real XML payload shape, request rate, batch size, and idempotency-key deduplication.

## With more time

- Add structured metrics and health reporting for production monitoring.
- Add richer checkpoint integrity validation, for example verifying stored idempotency keys when resuming.
- Add container packaging and CI automation for repeatable builds.
