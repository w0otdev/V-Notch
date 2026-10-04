using Xunit;

// STA test callers synchronously wait on the shared dispatcher. Limit the
// number of blocked runners so async cancellation and queue consumers still
// get worker threads promptly, including during coverage instrumentation.
[assembly: CollectionBehavior(MaxParallelThreads = 4)]
