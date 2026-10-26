using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using ExcelDataReader;
using ExcelNumberFormat;
using D = DocumentFormat.OpenXml.Drawing;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace AIHappey.Desktop.Core;

internal enum OfficeDocumentFormat { Word, Excel, PowerPoint }

internal sealed class OfficeDocumentTextExtractor(OfficeDocumentFormat format) : DocumentFormatTextExtractor(
    format switch
    {
        OfficeDocumentFormat.Word => [".docx", ".docm", ".dotx", ".dotm"],
        OfficeDocumentFormat.Excel => [".xlsx", ".xlsm", ".xltx", ".xltm"],
        _ => [".pptx", ".pptm", ".potx", ".potm", ".ppsx", ".ppsm"]
    }, format switch
    {
        OfficeDocumentFormat.Word => ["application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "application/vnd.ms-word.document.macroenabled.12", "application/vnd.openxmlformats-officedocument.wordprocessingml.template",
            "application/vnd.ms-word.template.macroenabled.12"],
        OfficeDocumentFormat.Excel => ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "application/vnd.ms-excel.sheet.macroenabled.12", "application/vnd.openxmlformats-officedocument.spreadsheetml.template",
            "application/vnd.ms-excel.template.macroenabled.12"],
        _ => ["application/vnd.openxmlformats-officedocument.presentationml.presentation", "application/vnd.ms-powerpoint.presentation.macroenabled.12",
            "application/vnd.openxmlformats-officedocument.presentationml.template", "application/vnd.ms-powerpoint.template.macroenabled.12",
            "application/vnd.openxmlformats-officedocument.presentationml.slideshow", "application/vnd.ms-powerpoint.slideshow.macroenabled.12"]
    })
{
    internal override Task<string?> ExtractAsync(ReadOnlyMemory<byte> content, DocumentExtractionContext context, int depth)
    {
        context.ValidatePackage(content);
        using var input = new MemoryStream(content.ToArray(), false);
        var text = new DocumentTextBuilder(context);
        switch (format)
        {
            case OfficeDocumentFormat.Word: Word(input, text, context); break;
            case OfficeDocumentFormat.Excel: Excel(input, text, context); break;
            default: PowerPoint(input, text, context); break;
        }
        return Task.FromResult<string?>(text.ToString().Trim());
    }

    private static void Word(Stream input, DocumentTextBuilder text, DocumentExtractionContext context)
    {
        using var document = WordprocessingDocument.Open(input, false, new OpenSettings { MaxCharactersInPart = DocumentExtractionContext.MaximumExpandedBytes });
        var main = document.MainDocumentPart ?? throw new InvalidDataException("Missing Word document part.");
        if (main.Document.Body is { } body) WordBlocks(body, text, context);
        foreach (var header in main.HeaderParts) WordBlocks(header.Header, text, context);
        foreach (var footer in main.FooterParts) WordBlocks(footer.Footer, text, context);
        if (main.FootnotesPart?.Footnotes is { } footnotes)
            foreach (var note in footnotes.Elements<W.Footnote>().Where(n => n.Id?.Value > 0)) WordBlocks(note, text, context);
        if (main.EndnotesPart?.Endnotes is { } endnotes)
            foreach (var note in endnotes.Elements<W.Endnote>().Where(n => n.Id?.Value > 0)) WordBlocks(note, text, context);
    }

    private static void WordBlocks(OpenXmlElement parent, DocumentTextBuilder text, DocumentExtractionContext context)
    {
        foreach (var child in parent.ChildElements)
        {
            context.Check();
            if (child is W.Paragraph paragraph) { WordRuns(paragraph, text, context); text.Line(); }
            else if (child is W.Table table)
            {
                foreach (var row in table.Elements<W.TableRow>())
                {
                    var first = true;
                    foreach (var cell in row.Elements<W.TableCell>())
                    {
                        if (!first) text.Append("\t");
                        first = false;
                        var value = new DocumentTextBuilder(context);
                        WordBlocks(cell, value, context);
                        text.Append(value.ToString().TrimEnd('\r', '\n'));
                    }
                    text.Line();
                }
            }
            else WordBlocks(child, text, context);
        }
    }

    private static void WordRuns(OpenXmlElement parent, DocumentTextBuilder text, DocumentExtractionContext context)
    {
        foreach (var child in parent.ChildElements)
        {
            context.Check();
            switch (child)
            {
                case W.Text value: text.Append(value.Text); break;
                case W.TabChar: text.Append("\t"); break;
                case W.Break or W.CarriageReturn: text.Line(); break;
                case W.DeletedRun: break;
                default: WordRuns(child, text, context); break;
            }
        }
    }

    private static void PowerPoint(Stream input, DocumentTextBuilder text, DocumentExtractionContext context)
    {
        using var document = PresentationDocument.Open(input, false, new OpenSettings { MaxCharactersInPart = DocumentExtractionContext.MaximumExpandedBytes });
        var part = document.PresentationPart ?? throw new InvalidDataException("Missing presentation part.");
        var index = 0;
        foreach (var id in part.Presentation.SlideIdList?.Elements<SlideId>() ?? [])
        {
            context.Check(); index++;
            if (id.RelationshipId?.Value is not { } relationship || part.GetPartById(relationship) is not SlidePart slide)
                throw new InvalidDataException("Missing slide relationship.");
            // Match the reference decoder's default: skip hidden slides, retaining source slide numbers.
            if (slide.Slide.Show?.Value == false) continue;
            var body = new DocumentTextBuilder(context);
            foreach (var paragraph in slide.Slide.Descendants<D.Paragraph>())
            {
                context.Check();
                foreach (var child in paragraph.ChildElements)
                {
                    if (child is D.Break) body.Line();
                    else foreach (var value in child.Descendants<D.Text>()) body.Append(value.Text);
                }
                body.Line();
            }
            if (!string.IsNullOrWhiteSpace(body.ToString()))
            { text.Line($"[Slide {index}]"); text.Line(body.ToString().Trim()); text.Line(); }
        }
    }

    private static void Excel(Stream input, DocumentTextBuilder text, DocumentExtractionContext context)
    {
        // Do not call RecalculateAllFormulas/GetFormattedString/Value: reading must never evaluate
        // formulas, refresh external links, execute macros, or change the supplied workbook.
        using var workbook = new XLWorkbook(input, new LoadOptions { RecalculateAllFormulas = false });
        foreach (var sheet in workbook.Worksheets)
        {
            context.Check();
            var range = sheet.RangeUsed(XLCellsUsedOptions.Contents);
            if (range is null) continue;
            text.Line($"[Worksheet: {DocumentExtractionContext.Label(sheet.Name)}]");
            foreach (var row in range.RowsUsed(XLCellsUsedOptions.Contents))
            {
                context.Check();
                // Keep empty leading/intermediate cells: otherwise column relationships disappear.
                for (var column = 1; column <= range.RangeAddress.LastAddress.ColumnNumber; column++)
                {
                    if (column > 1) text.Append("\t");
                    var cell = sheet.Cell(row.RowNumber(), column);
                    var value = cell.CachedValue;
                    object? raw = value.Type switch
                    {
                        XLDataType.Blank => null, XLDataType.Boolean => value.GetBoolean(), XLDataType.Number => value.GetNumber(),
                        XLDataType.DateTime => value.GetDateTime(), XLDataType.TimeSpan => value.GetTimeSpan(),
                        XLDataType.Error => value.GetError().ToString(), _ => value.GetText()
                    };
                    var code = cell.Style.NumberFormat.Format;
                    if (string.IsNullOrEmpty(code)) code = BuiltInNumberFormat(cell.Style.NumberFormat.NumberFormatId, raw);
                    text.Append(FormatCell(raw, code));
                }
                text.Line();
            }
            text.Line();
        }
    }

    internal static string FormatCell(object? value, string? code)
    {
        if (value is null) return "";
        if (value is bool boolean) return boolean ? "TRUE" : "FALSE";
        if (value is string str) return str;
        if (!string.IsNullOrEmpty(code))
        {
            var format = new NumberFormat(code);
            if (format.IsValid) return format.Format(value, CultureInfo.InvariantCulture);
        }
        return value switch
        {
            DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            TimeSpan time => time.ToString("c", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString() ?? ""
        };
    }

    private static string? BuiltInNumberFormat(int id, object? value) => id switch
    {
        1 => "0", 2 => "0.00", 3 => "#,##0", 4 => "#,##0.00", 9 => "0%", 10 => "0.00%", 11 => "0.00E+00",
        12 => "# ?/?", 13 => "# ??/??", 14 => "mm-dd-yy", 15 => "d-mmm-yy", 16 => "d-mmm", 17 => "mmm-yy",
        18 => "h:mm AM/PM", 19 => "h:mm:ss AM/PM", 20 => "h:mm", 21 => "h:mm:ss", 22 => "m/d/yy h:mm",
        45 => "mm:ss", 46 => "[h]:mm:ss", 47 => "mmss.0", 48 => "##0.0E+0", 49 => "@",
        _ => value is DateTime ? "yyyy-mm-dd hh:mm:ss" : value is TimeSpan ? "[h]:mm:ss" : null
    };
}

internal sealed class LegacyExcelDocumentTextExtractor() : DocumentFormatTextExtractor([".xls"], ["application/vnd.ms-excel"])
{
    internal override Task<string?> ExtractAsync(ReadOnlyMemory<byte> content, DocumentExtractionContext context, int depth)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var input = new MemoryStream(content.ToArray(), false);
        using var workbook = ExcelReaderFactory.CreateBinaryReader(input, new ExcelReaderConfiguration { LeaveOpen = true });
        var text = new DocumentTextBuilder(context);
        do
        {
            context.Check();
            var body = new DocumentTextBuilder(context);
            while (workbook.Read())
            {
                context.Check();
                var last = workbook.FieldCount - 1;
                while (last >= 0 && workbook.GetValue(last) is null) last--;
                if (last < 0) continue;
                for (var column = 0; column <= last; column++)
                {
                    if (column > 0) body.Append("\t");
                    body.Append(OfficeDocumentTextExtractor.FormatCell(workbook.GetValue(column), workbook.GetNumberFormatString(column)));
                }
                body.Line();
            }
            if (body.Length > 0) { text.Line($"[Worksheet: {DocumentExtractionContext.Label(workbook.Name)}]"); text.Line(body.ToString().TrimEnd()); }
        } while (workbook.NextResult());
        return Task.FromResult<string?>(text.ToString().Trim());
    }
}
