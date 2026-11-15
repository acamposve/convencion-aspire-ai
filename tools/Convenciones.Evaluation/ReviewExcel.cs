using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Convenciones.Evaluation;

/// <summary>One problem found in a review spreadsheet, addressed to the row the reviewer can see.</summary>
public sealed record ExcelCellError(int Row, string Field, string Message)
{
    public override string ToString() => Row == 0
        ? $"{Field}: {Message}"
        : $"Fila {Row} · {Field}: {Message}";
}

public sealed class ReviewExcelException(IReadOnlyList<ExcelCellError> errors)
    : Exception("La planilla de revisión tiene errores.")
{
    public IReadOnlyList<ExcelCellError> Errors { get; } = errors;
}

/// <summary>
/// Builds and reads the reviewer spreadsheet. The workbook is a presentation layer only: it produces the
/// same <see cref="ReviewPackage"/> JSON that <c>review-import</c> already accepts, and every semantic
/// rule is still enforced by <see cref="ReviewWorkflow.ValidateSubmission"/>.
/// </summary>
public static class ReviewExcelWorkbook
{
    public const string AnnotationSheet = "Anotacion";
    public const string TaxonomySheet = "Taxonomia";

    private const int HeaderRow = 7;
    private const int FirstDataRow = 8;
    private const string IdentityLabel = "Reviewer identity (obligatorio):";
    private const string VersionLabel = "Submission version:";

    private static readonly string[] Headers =
    [
        "Clause ID (no modificar)",
        "Documento",
        "Tipo de contrato",
        "Número",
        "Encabezado",
        "Página desde",
        "Página hasta",
        "Texto de la cláusula",
        "Categorías (separadas por comas)",
        "Categoría principal",
        "¿Ambigua? (Sí/No)",
        "Notas / justificación",
    ];

    /// <summary>Names and descriptions of taxonomy version 20260109000100, for the reference sheet only.</summary>
    private static readonly (string Key, string Name, string Description)[] TaxonomyReference =
    [
        ("parties", "Partes y definiciones", "Partes contratantes, definiciones y ámbito del convenio"),
        ("scope", "Alcance y obligaciones", "Objeto, alcance y obligaciones de las partes"),
        ("pricing", "Precio y pago", "Salarios, aumentos, pagos y condiciones económicas"),
        ("duration", "Duración y renovación", "Vigencia, prórroga y renovación"),
        ("termination", "Terminación", "Causas y efectos de la terminación"),
        ("confidentiality", "Confidencialidad", "Reserva y manejo de información"),
        ("data_protection", "Protección de datos", "Tratamiento de datos personales"),
        ("ip", "Propiedad intelectual", "Titularidad y uso de propiedad intelectual"),
        ("liability", "Responsabilidad e indemnización", "Responsabilidad, daños e indemnización"),
        ("warranties", "Garantías", "Garantías y declaraciones"),
        ("disputes", "Resolución de disputas", "Procedimientos de solución de controversias"),
        ("governing_law", "Ley aplicable", "Ley y jurisdicción aplicables"),
        ("force_majeure", "Fuerza mayor", "Eventos de fuerza mayor y sus efectos"),
        ("other", "Otros / Sin clasificar", "Cláusulas sin categoría identificable"),
    ];

    public static void Write(string path, EvaluationDataset dataset, ReviewPackage package, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(package);

        if (File.Exists(path))
        {
            if (!overwrite)
            {
                throw new EvaluationInputException(
                    $"Ya existe un archivo en {path}. No se sobrescribe: use otro nombre.");
            }

            File.Delete(path);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();

        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = CreateStylesheet();
        stylesPart.Stylesheet.Save();

        var annotationPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();

        sheetData.Append(Row(1, TitleCell("A1", $"Revisión de cláusulas — Revisor {package.ReviewerSlot}")));
        sheetData.Append(Row(2, TextCell("A2", "Corpus: " + package.CorpusId + " · versión de datos: " + package.DatasetVersion, 2)));
        sheetData.Append(Row(3,
            TextCell("A3", IdentityLabel, 4),
            TextCell("B3", package.ReviewerIdentity, 2)));
        sheetData.Append(Row(4,
            TextCell("A4", VersionLabel, 4),
            TextCell("B4", package.SubmissionVersion, 2)));
        sheetData.Append(Row(5,
            TextCell("A5", "Cómo completar:", 4),
            TextCell("B5", "Escriba una o varias categorías separadas por comas en la columna I. " +
                "La categoría principal (columna J) debe ser una de las que escribió. " +
                "Si ninguna categoría aplica, deje I y J vacías. Corrija las páginas en F y G si no " +
                "coinciden con el PDF. Marque K con Sí o No.", 2)));
        sheetData.Append(Row(6, TextCell("A6", "Las columnas A a E y H son de referencia: no las modifique. " +
            "Las columnas con fondo amarillo son las que usted completa.", 6)));

        var headerCells = Headers.Select((header, index) => TextCell(Column(index + 1) + HeaderRow, header, 1)).ToArray();
        sheetData.Append(Row(HeaderRow, headerCells));

        var rowNumber = FirstDataRow;
        foreach (var clause in package.Clauses)
        {
            sheetData.Append(Row(rowNumber,
                TextCell("A" + rowNumber, clause.ClauseId, 5),
                TextCell("B" + rowNumber, clause.DocumentId, 2),
                TextCell("C" + rowNumber, clause.ContractType, 2),
                TextCell("D" + rowNumber, clause.Number, 2),
                TextCell("E" + rowNumber, clause.Heading, 2),
                TextCell("F" + rowNumber, clause.PageFrom.ToString(CultureInfo.InvariantCulture), 7),
                TextCell("G" + rowNumber, clause.PageTo.ToString(CultureInfo.InvariantCulture), 7),
                TextCell("H" + rowNumber, clause.Text, 2),
                TextCell("I" + rowNumber, string.Join(", ", clause.Categories), 7),
                TextCell("J" + rowNumber, clause.Primary, 7),
                TextCell("K" + rowNumber, clause.Ambiguous ? "Sí" : string.Empty, 7),
                TextCell("L" + rowNumber, clause.Notes, 7)));
            rowNumber++;
        }

        annotationPart.Worksheet = new Worksheet(
            new SheetViews(new SheetView(
                new Pane
                {
                    VerticalSplit = HeaderRow,
                    TopLeftCell = "A" + (HeaderRow + 1),
                    ActivePane = PaneValues.BottomLeft,
                    State = PaneStateValues.Frozen,
                })
            { WorkbookViewId = 0 }),
            new Columns(
                new Column { Min = 1, Max = 1, Width = 14, CustomWidth = true },
                new Column { Min = 2, Max = 2, Width = 26, CustomWidth = true },
                new Column { Min = 3, Max = 3, Width = 14, CustomWidth = true },
                new Column { Min = 4, Max = 4, Width = 18, CustomWidth = true },
                new Column { Min = 5, Max = 5, Width = 20, CustomWidth = true },
                new Column { Min = 6, Max = 7, Width = 11, CustomWidth = true },
                new Column { Min = 8, Max = 8, Width = 70, CustomWidth = true },
                new Column { Min = 9, Max = 9, Width = 32, CustomWidth = true },
                new Column { Min = 10, Max = 10, Width = 22, CustomWidth = true },
                new Column { Min = 11, Max = 11, Width = 16, CustomWidth = true },
                new Column { Min = 12, Max = 12, Width = 40, CustomWidth = true }),
            sheetData);

        var taxonomyPart = workbookPart.AddNewPart<WorksheetPart>();
        var taxonomyData = new SheetData();
        taxonomyData.Append(Row(1,
            TextCell("A1", "Clave", 1),
            TextCell("B1", "Nombre", 1),
            TextCell("C1", "Descripción", 1),
            TextCell("D1", "¿Se usa en esta revisión?", 1)));

        var known = new HashSet<string>(package.TaxonomyKeys, StringComparer.OrdinalIgnoreCase);
        var taxonomyRow = 2;
        foreach (var (key, name, description) in TaxonomyReference)
        {
            taxonomyData.Append(Row(taxonomyRow,
                TextCell($"A{taxonomyRow}", key, 5),
                TextCell($"B{taxonomyRow}", name, 2),
                TextCell($"C{taxonomyRow}", description, 2),
                TextCell($"D{taxonomyRow}", known.Contains(key) ? "Sí" : "No", 2)));
            taxonomyRow++;
        }

        taxonomyPart.Worksheet = new Worksheet(
            new Columns(
                new Column { Min = 1, Max = 1, Width = 20, CustomWidth = true },
                new Column { Min = 2, Max = 2, Width = 30, CustomWidth = true },
                new Column { Min = 3, Max = 3, Width = 60, CustomWidth = true },
                new Column { Min = 4, Max = 4, Width = 20, CustomWidth = true }),
            taxonomyData);

        workbookPart.Workbook.AppendChild(new Sheets(
            new Sheet { Id = workbookPart.GetIdOfPart(annotationPart), SheetId = 1, Name = AnnotationSheet },
            new Sheet { Id = workbookPart.GetIdOfPart(taxonomyPart), SheetId = 2, Name = TaxonomySheet }));

        workbookPart.Workbook.Save();
    }

    public static ReviewPackage Read(string path, EvaluationDataset dataset, string slot)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        if (!File.Exists(path))
        {
            throw new EvaluationInputException($"No se encontró el archivo: {path}");
        }

        using var document = SpreadsheetDocument.Open(path, false);
        var workbookPart = document.WorkbookPart
            ?? throw new EvaluationInputException("El archivo no es un libro de Excel válido.");

        var sheets = workbookPart.Workbook?.Sheets?.Elements<Sheet>().ToList() ?? [];
        var sheet = sheets.FirstOrDefault(candidate => candidate.Name == AnnotationSheet) ?? sheets.FirstOrDefault()
            ?? throw new EvaluationInputException("El libro no tiene hojas.");

        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!);
        var rows = worksheetPart.Worksheet?.Descendants<Row>().ToList() ?? [];
        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;

        var errors = new List<ExcelCellError>();

        var header = rows.FirstOrDefault(row => CellText(row, 1, sharedStrings).StartsWith("Clause ID", StringComparison.OrdinalIgnoreCase))
            ?? throw new EvaluationInputException(
                "No se encontró la fila de encabezados (\"Clause ID\"). Use la plantilla original sin borrar filas.");

        var headerIndex = header.RowIndex?.Value ?? (uint)HeaderRow;

        Row? FindLabelledRow(string label) => rows.FirstOrDefault(row =>
            row.RowIndex is not null
            && row.RowIndex.Value < headerIndex
            && CellText(row, 1, sharedStrings).StartsWith(label, StringComparison.OrdinalIgnoreCase));

        var identityRow = FindLabelledRow("Reviewer identity");
        var reviewerIdentity = identityRow is null ? string.Empty : CellText(identityRow, 2, sharedStrings).Trim();
        if (string.IsNullOrWhiteSpace(reviewerIdentity))
        {
            errors.Add(new ExcelCellError(3, "Reviewer identity", "Escriba su nombre o identificador en la celda B3."));
        }

        var versionRow = FindLabelledRow("Submission version");
        var submissionVersion = versionRow is null ? "1" : CellText(versionRow, 2, sharedStrings).Trim();
        if (string.IsNullOrWhiteSpace(submissionVersion))
        {
            submissionVersion = "1";
        }

        var taxonomy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in dataset.TaxonomyKeys ?? [])
        {
            taxonomy[key] = key;
        }

        var package = ReviewWorkflow.ForReviewer(dataset, slot);
        var expected = package.Clauses.ToDictionary(clause => clause.ClauseId, StringComparer.Ordinal);
        var answers = new List<ReviewClause>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var row in rows.Where(row => row.RowIndex is not null && row.RowIndex.Value > headerIndex))
        {
            var rowNumber = (int)row.RowIndex!.Value;
            var clauseId = CellText(row, 1, sharedStrings).Trim();

            if (string.IsNullOrWhiteSpace(clauseId))
            {
                // A whole blank row is padding; a row with answers but no id is a reviewer mistake.
                var hasAnswer = new[] { 9, 10, 11, 12 }.Any(column => !string.IsNullOrWhiteSpace(CellText(row, column, sharedStrings)));
                if (hasAnswer)
                {
                    errors.Add(new ExcelCellError(rowNumber, "Clause ID", "Fila con respuestas pero sin Clause ID."));
                }

                continue;
            }

            if (seen.TryGetValue(clauseId, out var firstRow))
            {
                errors.Add(new ExcelCellError(rowNumber, "Clause ID",
                    $"Cláusula duplicada: '{clauseId}' ya aparece en la fila {firstRow}."));
                continue;
            }

            seen[clauseId] = rowNumber;

            if (!expected.TryGetValue(clauseId, out var template))
            {
                errors.Add(new ExcelCellError(rowNumber, "Clause ID",
                    $"Cláusula desconocida: '{clauseId}' no pertenece a este corpus."));
                continue;
            }

            var selected = new List<string>();
            foreach (var token in CellText(row, 9, sharedStrings)
                         .Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (taxonomy.TryGetValue(token, out var canonical))
                {
                    if (!selected.Contains(canonical, StringComparer.OrdinalIgnoreCase))
                    {
                        selected.Add(canonical);
                    }
                }
                else
                {
                    errors.Add(new ExcelCellError(rowNumber, "Categorías",
                        $"Categoría desconocida: '{token}'. Use solamente las claves de la hoja {TaxonomySheet}."));
                }
            }

            var pageFrom = ParsePage(CellText(row, 6, sharedStrings), rowNumber, "Página desde", errors);
            var pageTo = ParsePage(CellText(row, 7, sharedStrings), rowNumber, "Página hasta", errors);
            if (pageFrom is not null && pageTo is not null && pageTo < pageFrom)
            {
                errors.Add(new ExcelCellError(rowNumber, "Páginas",
                    $"La página final ({pageTo}) no puede ser menor que la inicial ({pageFrom})."));
            }

            var primaryText = CellText(row, 10, sharedStrings).Trim();
            string? primary = null;
            if (primaryText.Length > 0)
            {
                if (!taxonomy.TryGetValue(primaryText, out var canonicalPrimary))
                {
                    errors.Add(new ExcelCellError(rowNumber, "Categoría principal",
                        $"Categoría desconocida: '{primaryText}'."));
                }
                else if (!selected.Contains(canonicalPrimary, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add(new ExcelCellError(rowNumber, "Categoría principal",
                        $"'{canonicalPrimary}' no está entre las categorías seleccionadas en la columna H."));
                }
                else
                {
                    primary = canonicalPrimary;
                }
            }

            var ambiguous = ParseAmbiguous(CellText(row, 11, sharedStrings), rowNumber, errors);
            var notes = CellText(row, 12, sharedStrings).Trim();

            answers.Add(template with
            {
                PageFrom = pageFrom ?? template.PageFrom,
                PageTo = pageTo ?? template.PageTo,
                Categories = selected,
                Primary = primary,
                Ambiguous = ambiguous,
                Notes = notes.Length == 0 ? null : notes,
            });
        }

        foreach (var missing in expected.Keys.Where(id => !seen.ContainsKey(id)))
        {
            errors.Add(new ExcelCellError(0, "Clause ID",
                $"Falta la cláusula '{missing}': no borre filas de la plantilla."));
        }

        // A named but untouched sheet would otherwise read as "every clause is unclassifiable",
        // which is indistinguishable from an unedited file. Require at least one real answer.
        if (answers.Count > 0
            && answers.All(answer => answer.Categories.Count == 0 && !answer.Ambiguous && string.IsNullOrEmpty(answer.Notes)))
        {
            errors.Add(new ExcelCellError(0, "Planilla vacía",
                "Ninguna cláusula tiene categorías, notas ni marca de ambigüedad. " +
                "Complete la columna H, o explique en la columna K por qué ninguna categoría aplica."));
        }

        if (errors.Count > 0)
        {
            throw new ReviewExcelException(errors);
        }

        var submission = package with
        {
            ReviewerIdentity = reviewerIdentity,
            SubmissionVersion = submissionVersion,
            Status = "submitted",
            Clauses = answers,
        };

        // The authoritative rules still run here, so the spreadsheet cannot become a way around them.
        var problems = ReviewWorkflow.ValidateSubmission(dataset, submission);
        if (problems.Count > 0)
        {
            throw new ReviewExcelException(problems
                .Select(problem => new ExcelCellError(0, "Validación", problem))
                .ToList());
        }

        return submission;
    }

    private static bool ParseAmbiguous(string raw, int row, List<ExcelCellError> errors)
    {
        var value = raw.Trim();
        if (value.Length == 0)
        {
            return false;
        }

        var normalized = value.ToUpperInvariant();
        if (normalized is "SÍ" or "SI" or "S" or "YES" or "Y" or "TRUE" or "1" or "VERDADERO")
        {
            return true;
        }

        if (normalized is "NO" or "N" or "FALSE" or "0" or "FALSO")
        {
            return false;
        }

        errors.Add(new ExcelCellError(row, "¿Ambigua?",
            $"Valor no válido: '{value}'. Escriba Sí o No."));
        return false;
    }

    private static int? ParsePage(string raw, int row, string field, List<ExcelCellError> errors)
    {
        var value = raw.Trim();
        if (value.Length == 0)
        {
            errors.Add(new ExcelCellError(row, field, "Escriba un número de página (1 o mayor)."));
            return null;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var page) || page < 1)
        {
            errors.Add(new ExcelCellError(row, field,
                $"Valor no válido: '{value}'. Escriba un número de página (1 o mayor)."));
            return null;
        }

        return page;
    }

    private static Row Row(int index, params Cell[] cells) =>
        new(cells) { RowIndex = (uint)index };

    private static Cell TextCell(string reference, string? value, uint style)
    {
        var cell = new Cell { CellReference = reference, StyleIndex = style };
        if (!string.IsNullOrEmpty(value))
        {
            cell.DataType = CellValues.InlineString;
            cell.InlineString = new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve });
        }

        return cell;
    }

    private static Cell TitleCell(string reference, string value) =>
        TextCell(reference, value, 3);

    private static string CellText(Row row, int column, SharedStringTable? sharedStrings)
    {
        var reference = Column(column) + row.RowIndex;
        var cell = row.Elements<Cell>().FirstOrDefault(candidate =>
            string.Equals(candidate.CellReference?.Value, reference, StringComparison.OrdinalIgnoreCase));

        if (cell is null)
        {
            return string.Empty;
        }

        if (cell.DataType?.Value == CellValues.SharedString && sharedStrings is not null)
        {
            var index = int.TryParse(cell.CellValue?.InnerText, out var parsed) ? parsed : -1;
            return index >= 0 && index < sharedStrings.ChildElements.Count
                ? sharedStrings.ChildElements[index].InnerText
                : string.Empty;
        }

        return cell.InlineString?.InnerText ?? cell.CellValue?.InnerText ?? string.Empty;
    }

    private static string Column(int index)
    {
        var name = string.Empty;
        while (index > 0)
        {
            var remainder = (index - 1) % 26;
            name = (char)('A' + remainder) + name;
            index = (index - 1) / 26;
        }

        return name;
    }

    private static Stylesheet CreateStylesheet()
    {
        var fonts = new Fonts(
            new Font(new FontSize { Val = 11 }),
            new Font(new Bold(), new FontSize { Val = 11 }),
            new Font(new Bold(), new FontSize { Val = 14 }))
        { Count = 3 };

        var fills = new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FFD9E1F2" }) { PatternType = PatternValues.Solid }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FFF2F2F2" }) { PatternType = PatternValues.Solid }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FFFFF2CC" }) { PatternType = PatternValues.Solid }))
        { Count = 5 };

        var borders = new Borders(
            new Border(),
            new Border(
                new LeftBorder(new Color { Auto = true }) { Style = BorderStyleValues.Thin },
                new RightBorder(new Color { Auto = true }) { Style = BorderStyleValues.Thin },
                new TopBorder(new Color { Auto = true }) { Style = BorderStyleValues.Thin },
                new BottomBorder(new Color { Auto = true }) { Style = BorderStyleValues.Thin }))
        { Count = 2 };

        var cellFormats = new CellFormats(
            new CellFormat { FontId = 0, FillId = 0, BorderId = 0 },
            new CellFormat
            {
                FontId = 1, FillId = 2, BorderId = 1, ApplyFont = true, ApplyFill = true, ApplyBorder = true,
                Alignment = new Alignment { WrapText = true, Vertical = VerticalAlignmentValues.Center, Horizontal = HorizontalAlignmentValues.Center },
                ApplyAlignment = true,
            },
            new CellFormat
            {
                FontId = 0, FillId = 0, BorderId = 1, ApplyBorder = true,
                Alignment = new Alignment { WrapText = true, Vertical = VerticalAlignmentValues.Top },
                ApplyAlignment = true,
            },
            new CellFormat { FontId = 2, FillId = 0, BorderId = 0, ApplyFont = true },
            new CellFormat { FontId = 1, FillId = 0, BorderId = 0, ApplyFont = true },
            new CellFormat
            {
                FontId = 0, FillId = 3, BorderId = 1, ApplyFill = true, ApplyBorder = true,
                Alignment = new Alignment { WrapText = true, Vertical = VerticalAlignmentValues.Top },
                ApplyAlignment = true,
            },
            new CellFormat
            {
                FontId = 0, FillId = 4, BorderId = 1, ApplyFill = true, ApplyBorder = true,
                Alignment = new Alignment { WrapText = true },
                ApplyAlignment = true,
            },
            new CellFormat
            {
                FontId = 0, FillId = 4, BorderId = 1, ApplyFill = true, ApplyBorder = true,
                Alignment = new Alignment { WrapText = true, Vertical = VerticalAlignmentValues.Top },
                ApplyAlignment = true,
            })
        { Count = 8 };

        var cellStyleFormats = new CellStyleFormats(
            new CellFormat { FontId = 0, FillId = 0, BorderId = 0, NumberFormatId = 0 })
        { Count = 1 };

        var cellStyles = new CellStyles(
            new CellStyle { Name = "Normal", FormatId = 0, BuiltinId = 0 })
        { Count = 1 };

        return new Stylesheet(fonts, fills, borders, cellStyleFormats, cellFormats, cellStyles);
    }
}
