// Fixtures run in parallel; tests inside one fixture stay sequential, so fixture-level state
// (fields set up in [SetUp]/[OneTimeSetUp]) needs no locking. A fixture that touches anything
// process-wide - the current directory, environment variables, a global observer, a fixed
// temp path, sudo/device-mapper names - must either make that state its own or be marked
// [NonParallelizable] with the reason beside the attribute.
[assembly: Parallelizable(ParallelScope.Fixtures)]
