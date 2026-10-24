using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// PostgreSQL runs in a container locally. The image is pinned so every developer and CI run uses the
// same major version. No password is written here: Aspire generates one into the local secret store
// and injects it as ConnectionStrings__convenciones, so nothing credential-shaped is ever committed
// (constitution Art. VI.3).
var postgres = builder
    .AddPostgres("postgres")
    .WithImage("postgres", "17-alpine")
    .WithDataVolume("convenciones-pgdata")
    .WithPgAdmin();

var database = postgres.AddDatabase("convenciones");

// The schema is applied by a dedicated resource rather than by the API at startup:
//   * the API never needs DDL privileges;
//   * a failed migration stops the stack instead of surfacing as a confusing runtime error;
//   * the API waits for it, so it only starts against a database that is already complete.
var migrationsPath = Path.GetFullPath(
    Path.Combine(builder.AppHostDirectory, "..", "..", "supabase", "migrations"));

var migrator = builder
    .AddProject<Projects.Convenciones_Migrator>("migrator")
    .WithReference(database)
    .WithEnvironment("MIGRATIONS_PATH", migrationsPath)
    .WaitFor(database);

builder
    .AddProject<Projects.Convenciones_Api>("api")
    .WithReference(database)
    .WaitForCompletion(migrator);

builder.Build().Run();
