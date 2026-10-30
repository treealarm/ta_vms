namespace VmsMap.Tests;

// The classes that need a real PostgreSQL share one scratch database and truncate it before every
// test, so they must not run in parallel with each other.
[CollectionDefinition("postgis", DisableParallelization = true)]
public class PostgisCollection;
