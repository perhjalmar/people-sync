# Operations Guide

At 3 AM, first check the process exit code, application logs/stdout, `errors.jsonl`, and the checkpoint log. If the checkpoint stopped advancing, compare the last checkpointed batch index with System B request logs and confirm whether the same idempotency key already succeeded.

For recovery, rerun the job against the same input file and checkpoint file. The runner recomputes the file fingerprint, skips already checkpointed batches, and resends only missing batches with deterministic idempotency keys so System B can deduplicate anything accepted before the crash.
