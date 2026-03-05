using Xunit;

// All test classes that mutate DRIVECHILL_DATA_DIR must run sequentially to
// avoid process-global environment-variable races.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]
