using System.Text.Json;
using Convenciones.Backup;

const string Usage = """
    Convenciones backup tool

      create  --connection <cs> --output <dir> [--storage-root <dir>]
      verify  --directory <dir>
      restore --connection <cs> --directory <dir> --migrations <dir> --confirm <database> [--storage-root <dir>]
      check   --connection <cs> [--storage-root <dir>]

    Restores are only accepted into an empty database whose name was explicitly confirmed.
    """;

if (args.Length == 0)
{
    Console.Error.WriteLine(Usage);
    return 2;
}

var command = args[0];
var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (var index = 1; index < args.Length; index++)
{
    if (!args[index].StartsWith("--", StringComparison.Ordinal))
    {
        Console.Error.WriteLine(Usage);
        return 2;
    }

    var name = args[index][2..];
    if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
    {
        options[name] = args[++index];
    }
    else
    {
        options[name] = "true";
    }
}

string Required(string name) =>
    options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"Missing required option --{name}.");

try
{
    switch (command)
    {
        case "create":
        {
            var manifest = await BackupManager.CreateAsync(
                Required("connection"),
                Required("output"),
                options.GetValueOrDefault("storage-root"),
                CancellationToken.None);

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                manifest.SourceDatabase,
                manifest.MigrationVersion,
                tables = manifest.Tables.Count,
                rows = manifest.Tables.Sum(table => table.Rows),
                storageFiles = manifest.Storage?.Files.Count ?? 0,
            }));

            return 0;
        }

        case "verify":
        {
            var manifest = await BackupManager.VerifyAsync(Required("directory"), CancellationToken.None);
            Console.WriteLine($"verified {manifest.Tables.Count} table(s), created {manifest.CreatedAt:O}");
            return 0;
        }

        case "restore":
        {
            var report = await BackupManager.RestoreAsync(
                Required("connection"),
                Required("directory"),
                Required("confirm"),
                Required("migrations"),
                options.GetValueOrDefault("storage-root"),
                options.ContainsKey("replace"),
                CancellationToken.None);

            Console.WriteLine(JsonSerializer.Serialize(report));
            return 0;
        }

        case "check":
        {
            var report = await BackupManager.CheckAsync(
                Required("connection"),
                options.GetValueOrDefault("storage-root"),
                options.GetValueOrDefault("directory"),
                CancellationToken.None);

            Console.WriteLine(JsonSerializer.Serialize(report));
            return report.Passed ? 0 : 1;
        }

        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
catch (BackupIntegrityException exception)
{
    Console.Error.WriteLine($"integrity failure: {exception.Message}");
    return 3;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"failed: {exception.Message}");
    return 1;
}
