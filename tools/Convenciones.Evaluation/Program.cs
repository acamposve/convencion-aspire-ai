using System.Text;
using Convenciones.Evaluation;

// Review messages are Spanish; a UTF-8 console keeps the accents readable.
try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch (IOException)
{
    // Redirected or restricted console: the text is still written, only the encoding is left as-is.
}

// Evaluation mode: score captured predictions against a versioned corpus. No network, no provider,
// no database. The default mode below keeps profiling a PDF (or a directory of PDFs).
if (args.Length > 0 && args[0] is "evaluate")
{
    return Evaluate(args[1..]);
}

if (args.Length > 0 && args[0] is "coverage")
{
    return Coverage(args[1..]);
}

if (args.Length > 0 && args[0] is "review-export")
{
    return ReviewExport(args[1..]);
}

if (args.Length > 0 && args[0] is "review-import")
{
    return ReviewImport(args[1..]);
}

if (args.Length > 0 && args[0] is "review-adjudicate")
{
    return ReviewAdjudicate(args[1..]);
}

if (args.Length > 0 && args[0] is "review-template")
{
    return ReviewTemplate(args[1..]);
}

if (args.Length > 0 && args[0] is "review-excel")
{
    return ReviewExcel(args[1..]);
}

if (args.Length > 0 && args[0] is "corpus-materialize")
{
    return CorpusMaterialize(args[1..]);
}

if (args.Length > 0 && args[0] is "pdf-pages")
{
    return PdfPages(args[1..]);
}


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

static int Evaluate(string[] options)
{
    string? datasetPath = null;
    string? predictionsPath = null;
    string? jsonPath = null;
    string? markdownPath = null;

    for (var index = 0; index < options.Length; index++)
    {
        switch (options[index])
        {
            case "--dataset":
                datasetPath = Value(options, ref index);
                break;
            case "--predictions":
                predictionsPath = Value(options, ref index);
                break;
            case "--out-json":
                jsonPath = Value(options, ref index);
                break;
            case "--out-md":
                markdownPath = Value(options, ref index);
                break;
            default:
                Console.Error.WriteLine($"Unknown option: {options[index]}");
                return 2;
        }
    }

    if (datasetPath is null || predictionsPath is null)
    {
        Console.Error.WriteLine(
            "usage: evaluate --dataset <corpus.json> --predictions <predictions.json> " +
            "[--out-json <result.json>] [--out-md <report.md>]");
        return 2;
    }

    try
    {
        var dataset = EvaluationJson.Load<EvaluationDataset>(datasetPath);
        var predictions = EvaluationJson.Load<PredictionSet>(predictionsPath);
        var result = EvaluationEngine.Evaluate(dataset, predictions);

        var json = jsonPath ?? Path.ChangeExtension(predictionsPath, ".result.json");
        EvaluationReport.WriteJson(json, result);
        if (markdownPath is not null)
        {
            EvaluationReport.WriteMarkdown(markdownPath, result);
        }

        Console.WriteLine(
            $"extraction: matched {result.Extraction.Matched}/{result.Extraction.Annotated}, " +
            $"P={result.Extraction.Precision:F4} R={result.Extraction.Recall:F4} F1={result.Extraction.F1:F4}");
        Console.WriteLine(
            $"classification: {result.Classification.EvaluatedClauses} clauses, " +
            $"macro-F1={result.Classification.MacroF1:F4} micro-F1={result.Classification.MicroF1:F4}");
        Console.WriteLine($"wrote {json}" + (markdownPath is null ? string.Empty : $" and {markdownPath}"));
        return 0;
    }
    catch (EvaluationInputException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

static int ReviewTemplate(string[] options)
{
    string? datasetPath = null;
    string? slot = null;
    string? outputPath = null;
    var overwrite = false;

    for (var index = 0; index < options.Length; index++)
    {
        switch (options[index])
        {
            case "--dataset": datasetPath = Value(options, ref index); break;
            case "--slot": slot = Value(options, ref index); break;
            case "--out": outputPath = Value(options, ref index); break;
            case "--overwrite": overwrite = true; break;
            default:
                Console.Error.WriteLine($"Unknown option: {options[index]}");
                return 2;
        }
    }

    if (datasetPath is null || slot is null || outputPath is null)
    {
        Console.Error.WriteLine("usage: review-template --dataset <corpus.json> --slot A|B --out <template.xlsx> [--overwrite]");
        return 2;
    }

    try
    {
        var dataset = EvaluationJson.Load<EvaluationDataset>(datasetPath);
        var package = ReviewWorkflow.ForReviewer(dataset, slot);
        ReviewExcelWorkbook.Write(outputPath, dataset, package, overwrite);

        Console.WriteLine(
            $"template {slot}: {package.Clauses.Count} clauses, {package.TaxonomyKeys.Count} categories -> {outputPath}");
        Console.WriteLine("The reviewer fills columns H-K and B3, then the file is converted with review-excel.");
        return 0;
    }
    catch (EvaluationInputException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

static int ReviewExcel(string[] options)
{
    string? datasetPath = null;
    string? slot = null;
    string? inputPath = null;
    string? outputPath = null;

    for (var index = 0; index < options.Length; index++)
    {
        switch (options[index])
        {
            case "--dataset": datasetPath = Value(options, ref index); break;
            case "--slot": slot = Value(options, ref index); break;
            case "--input": inputPath = Value(options, ref index); break;
            case "--out": outputPath = Value(options, ref index); break;
            default:
                Console.Error.WriteLine($"Unknown option: {options[index]}");
                return 2;
        }
    }

    if (datasetPath is null || slot is null || inputPath is null || outputPath is null)
    {
        Console.Error.WriteLine(
            "usage: review-excel --dataset <corpus.json> --slot A|B --input <completed.xlsx> --out <submission.json>");
        return 2;
    }

    try
    {
        if (File.Exists(outputPath))
        {
            Console.Error.WriteLine($"Ya existe {outputPath}. No se sobrescribe: elija otro nombre.");
            return 3;
        }

        var dataset = EvaluationJson.Load<EvaluationDataset>(datasetPath);
        var submission = ReviewExcelWorkbook.Read(inputPath, dataset, slot);
        EvaluationJson.Save(outputPath, submission with { Status = "submitted" });

        Console.WriteLine(
            $"converted: {submission.Clauses.Count} clauses, reviewer '{submission.ReviewerIdentity}', " +
            $"version {submission.SubmissionVersion} -> {outputPath}");
        Console.WriteLine($"Now run: review-import --dataset {datasetPath} --submission {outputPath} --out <imported.json>");
        return 0;
    }
    catch (ReviewExcelException exception)
    {
        Console.Error.WriteLine(exception.Message);
        foreach (var error in exception.Errors)
        {
            Console.Error.WriteLine("  " + error);
        }

        return 4;
    }
    catch (EvaluationInputException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

static int CorpusMaterialize(string[] options)
{
    string? datasetPath = null;
    string? adjudicationPath = null;
    string? outputPath = null;
    var version = "2";
    string? note = null;

    for (var index = 0; index < options.Length; index++)
    {
        switch (options[index])
        {
            case "--dataset": datasetPath = Value(options, ref index); break;
            case "--adjudication": adjudicationPath = Value(options, ref index); break;
            case "--out": outputPath = Value(options, ref index); break;
            case "--version": version = Value(options, ref index) ?? "2"; break;
            case "--note": note = Value(options, ref index); break;
            default:
                Console.Error.WriteLine($"Unknown option: {options[index]}");
                return 2;
        }
    }

    if (datasetPath is null || adjudicationPath is null || outputPath is null)
    {
        Console.Error.WriteLine(
            "usage: corpus-materialize --dataset <source.dataset.json> --adjudication <finalized.json> " +
            "--out <corpus-v2.dataset.json> [--version 2] [--note <text>]");
        return 2;
    }

    try
    {
        var source = EvaluationJson.Load<EvaluationDataset>(datasetPath);
        var worksheet = EvaluationJson.Load<AdjudicationWorksheet>(adjudicationPath);
        var published = CorpusPublisher.Materialize(source, worksheet, version) with
        {
            Notes = note ?? source.Notes,
        };

        EvaluationValidator.ValidateDataset(published);
        EvaluationJson.Save(outputPath, published);

        var clauses = published.Documents.SelectMany(document => document.Clauses).ToList();
        var disagreements = worksheet.Rows.Count(row => row.Agreement == "disagree");
        Console.WriteLine(
            $"published {published.CorpusId} v{published.DatasetVersion}: {clauses.Count} clauses, " +
            $"{clauses.Count(clause => clause.Status == "reviewed")} reviewed, {disagreements} adjudicated disagreements");
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }
    catch (EvaluationInputException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

static int PdfPages(string[] options)
{
    string? datasetPath = null;
    var pdfDirectory = "docs/quality/corpus/pdf";
    string? outputPath = null;
    string? dumpDocument = null;

    for (var index = 0; index < options.Length; index++)
    {
        switch (options[index])
        {
            case "--dataset": datasetPath = Value(options, ref index); break;
            case "--pdf-dir": pdfDirectory = Value(options, ref index) ?? pdfDirectory; break;
            case "--out-json": outputPath = Value(options, ref index); break;
            case "--dump": dumpDocument = Value(options, ref index); break;
            default:
                Console.Error.WriteLine($"Unknown option: {options[index]}");
                return 2;
        }
    }

    if (datasetPath is null)
    {
        Console.Error.WriteLine("usage: pdf-pages --dataset <corpus.json> [--pdf-dir <dir>] [--out-json <f>]");
        return 2;
    }

    try
    {
        var dataset = EvaluationJson.Load<EvaluationDataset>(datasetPath);

        if (dumpDocument is not null)
        {
            var pages = PdfPageLocator.RenderPages(Path.Combine(pdfDirectory, dumpDocument + ".pdf"));
            for (var page = 0; page < pages.Count; page++)
            {
                Console.WriteLine($"--- page {page + 1} ---");
                Console.WriteLine(PdfPageLocator.Normalize(pages[page]));
            }

            return 0;
        }

        var spans = PdfPageLocator.Locate(dataset, pdfDirectory);

        Console.WriteLine("clause\tdraft\tpdf\tstatus");
        foreach (var span in spans)
        {
            var status = !span.Found ? "NOT FOUND" : span.MatchesDraft ? "ok" : "MISMATCH";
            Console.WriteLine(
                $"{span.ClauseId}\t{span.DraftFrom}-{span.DraftTo}\t" +
                $"{(span.Found ? $"{span.PdfFrom}-{span.PdfTo}" : "-")}\t{status}");
        }

        var mismatches = spans.Count(span => span.Found && !span.MatchesDraft);
        var missing = spans.Count(span => !span.Found);
        Console.WriteLine($"clauses: {spans.Count}, mismatches: {mismatches}, not found: {missing}");

        var json = outputPath ?? Path.ChangeExtension(datasetPath, ".pages.json");
        EvaluationJson.Save(json, spans);
        Console.WriteLine($"wrote {json}");
        return missing == 0 ? 0 : 4;
    }
    catch (Exception exception) when (exception is not OutOfMemoryException)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

static string? Value(string[] values, ref int index) =>
    index + 1 < values.Length ? values[++index] : null;

static int ReviewExport(string[] options)
{
    string? datasetPath = null;
    string? slot = null;
    string? jsonPath = null;
    string? markdownPath = null;

    for (var index = 0; index < options.Length; index++)
    {
        switch (options[index])
        {
            case "--dataset": datasetPath = Value(options, ref index); break;
            case "--slot": slot = Value(options, ref index); break;
            case "--out-json": jsonPath = Value(options, ref index); break;
            case "--out-md": markdownPath = Value(options, ref index); break;
            default:
                Console.Error.WriteLine($"Unknown option: {options[index]}");
                return 2;
        }
    }

    if (datasetPath is null || slot is null)
    {
        Console.Error.WriteLine("usage: review-export --dataset <corpus.json> --slot A|B [--out-json <f>] [--out-md <f>]");
        return 2;
    }

    try
    {
        var dataset = EvaluationJson.Load<EvaluationDataset>(datasetPath);
        var package = ReviewWorkflow.ForReviewer(dataset, slot);

        var json = jsonPath ?? $"review-{slot}.json";
        EvaluationJson.Save(json, package);
        var markdown = markdownPath ?? Path.ChangeExtension(json, ".md");
        File.WriteAllText(markdown, ReviewWorkflow.ToMarkdown(package) + Environment.NewLine);

        Console.WriteLine(
            $"review {slot}: {package.Clauses.Count} clauses, {package.TaxonomyKeys.Count} categories, blind (no draft answers)");
        Console.WriteLine($"wrote {json} and {markdown}");
        return 0;
    }
    catch (EvaluationInputException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

static int ReviewImport(string[] options)
{
    string? datasetPath = null;
    string? submissionPath = null;
    string? outputPath = null;

    for (var index = 0; index < options.Length; index++)
    {
        switch (options[index])
        {
            case "--dataset": datasetPath = Value(options, ref index); break;
            case "--submission": submissionPath = Value(options, ref index); break;
            case "--out": outputPath = Value(options, ref index); break;
            default:
                Console.Error.WriteLine($"Unknown option: {options[index]}");
                return 2;
        }
    }

    if (datasetPath is null || submissionPath is null || outputPath is null)
    {
        Console.Error.WriteLine("usage: review-import --dataset <corpus.json> --submission <file> --out <file>");
        return 2;
    }

    try
    {
        var dataset = EvaluationJson.Load<EvaluationDataset>(datasetPath);
        var submission = EvaluationJson.Load<ReviewPackage>(submissionPath);
        ReviewWorkflow.Import(dataset, submission, outputPath);

        Console.WriteLine($"imported reviewer {submission.ReviewerSlot} ({submission.ReviewerIdentity}) to {outputPath}");
        Console.WriteLine("This is a recorded submission, not accepted ground truth. Adjudication is a separate step.");
        return 0;
    }
    catch (EvaluationInputException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

static int ReviewAdjudicate(string[] options)
{
    string? datasetPath = null;
    string? aPath = null;
    string? bPath = null;
    string? jsonPath = null;
    string? markdownPath = null;

    for (var index = 0; index < options.Length; index++)
    {
        switch (options[index])
        {
            case "--dataset": datasetPath = Value(options, ref index); break;
            case "--a": aPath = Value(options, ref index); break;
            case "--b": bPath = Value(options, ref index); break;
            case "--out-json": jsonPath = Value(options, ref index); break;
            case "--out-md": markdownPath = Value(options, ref index); break;
            default:
                Console.Error.WriteLine($"Unknown option: {options[index]}");
                return 2;
        }
    }

    if (datasetPath is null || aPath is null || bPath is null)
    {
        Console.Error.WriteLine("usage: review-adjudicate --dataset <corpus.json> --a <file> --b <file> [--out-json <f>] [--out-md <f>]");
        return 2;
    }

    try
    {
        var dataset = EvaluationJson.Load<EvaluationDataset>(datasetPath);
        var reviewerA = EvaluationJson.Load<ReviewPackage>(aPath);
        var reviewerB = EvaluationJson.Load<ReviewPackage>(bPath);
        var worksheet = ReviewWorkflow.Adjudicate(dataset, reviewerA, reviewerB);

        var json = jsonPath ?? "adjudication.json";
        EvaluationJson.Save(json, worksheet);
        var markdown = markdownPath ?? Path.ChangeExtension(json, ".md");
        File.WriteAllText(markdown, ReviewWorkflow.ToMarkdown(worksheet) + Environment.NewLine);

        var disagreements = worksheet.Rows.Count(row => row.Agreement == "disagree");
        Console.WriteLine($"{worksheet.Rows.Count} rows, {disagreements} disagreements needing a human decision");
        Console.WriteLine($"wrote {json} and {markdown}");
        return 0;
    }
    catch (EvaluationInputException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}

static int Coverage(string[] options)
{
    string? datasetPath = null;
    string? markdownPath = null;

    for (var index = 0; index < options.Length; index++)
    {
        switch (options[index])
        {
            case "--dataset":
                datasetPath = Value(options, ref index);
                break;
            case "--out-md":
                markdownPath = Value(options, ref index);
                break;
            default:
                Console.Error.WriteLine($"Unknown option: {options[index]}");
                return 2;
        }
    }

    if (datasetPath is null)
    {
        Console.Error.WriteLine("usage: coverage --dataset <corpus.json> [--out-md <coverage.md>]");
        return 2;
    }

    try
    {
        var dataset = EvaluationJson.Load<EvaluationDataset>(datasetPath);
        var coverage = CorpusCoverageReport.Build(dataset);

        var markdown = markdownPath ?? Path.ChangeExtension(datasetPath, ".coverage.md");
        CorpusCoverageReport.WriteMarkdown(markdown, coverage);

        var uncovered = coverage.Categories.Count(category => category.Clauses == 0);
        Console.WriteLine(
            $"{coverage.CorpusId} v{coverage.DatasetVersion}: {coverage.Documents} documents, " +
            $"{coverage.Clauses} clauses ({coverage.ReviewedClauses} reviewed, {coverage.DraftClauses} draft), " +
            $"{coverage.Categories.Count - uncovered}/{coverage.Categories.Count} categories covered");
        Console.WriteLine($"wrote {markdown}");
        return 0;
    }
    catch (EvaluationInputException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }
}
