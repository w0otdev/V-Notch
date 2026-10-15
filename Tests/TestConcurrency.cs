using Xunit;

// STA test callers are grouped in the serialized Localization collection so
// only one runner blocks on the shared dispatcher at a time, leaving worker
// threads free for async continuations and non-UI tests during coverage runs.
[assembly: CollectionBehavior(MaxParallelThreads = 4)]

