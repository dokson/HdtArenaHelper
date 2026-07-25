using Xunit;

// Tests in this assembly run SEQUENTIALLY. Not a style choice and not slowness insurance: the code
// under test logs through HDT's own `Log.WriteLine`, which enqueues into an unsynchronised
// `Queue<string>`. Run two logging test classes in parallel and that queue corrupts —
// "Destination array was not long enough" out of `Queue.Enqueue`, thrown from inside HDT, with a
// stack trace that looks like our bug and is not. It is their logger, we cannot fix it, so we must
// not call it concurrently.
//
// It surfaced as a CI-only failure while the same suite was green locally: core count and timing
// decide whether the race is hit, which is exactly why this must be pinned rather than retried.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
