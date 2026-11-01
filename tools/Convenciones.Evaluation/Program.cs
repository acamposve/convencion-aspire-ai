using Convenciones.Evaluation;

var root = args.Length > 0 ? args[0] : "docs/test";
if (!Directory.Exists(root) && !File.Exists(root))
{
    Console.Error.WriteLine($"No such file or directory: {root}");
    return 2;
}

var files = File.Exists(root)
    ? new[] { root }
    : Directory.GetFiles(root, "*.pdf", SearchOption.AllDirectories).OrderBy(path => path).ToArray();

if (files.Length == 0)
{
    Console.Error.WriteLine($"No PDF files under {root}.");
    return 2;
}

Console.WriteLine("file\tpages\tchars\tnonWS\tchars/page\tocr?\tms");
foreach (var file in files)
{
    try
    {
        var profile = DocumentProfiler.Profile(file);
        Console.WriteLine(
            $"{profile.FileName}\t{profile.Pages}\t{profile.Characters}\t{profile.NonWhitespaceCharacters}\t" +
            $"{profile.CharactersPerPage}\t{(profile.RequiresOcr ? "YES" : "no")}\t{profile.Duration.TotalMilliseconds:F0}");
    }
    catch (Exception exception) when (exception is not OutOfMemoryException)
    {
        Console.WriteLine($"{Path.GetFileName(file)}\tERROR\t{exception.Message}");
    }
}

return 0;
