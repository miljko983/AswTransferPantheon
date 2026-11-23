using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AswTransferToPantheon.Infrastructure.Configuration;
using AswTransferToPantheon.Infrastructure.Models;
using AswTransferToPantheon.Services.Helpers;

namespace AswTransferToPantheon.Services.Implementation;

public sealed partial class EmailNotificationService
{
    public async Task SendWmsDocumentSuccessEmail(
        string groupName, string taskName, WmsDocumentRunResult result, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(result);
        token.ThrowIfCancellationRequested();
        var kind = WmsKind(result);
        if (!result.HasNewWork)
            return;

        var key = kind == "KUF" ? "KufObrada.Completed" : "VlpObrada.Completed";
        if (!TryGetNotification(key, out var notification))
            return;

        var content = BuildWmsSuccessContent(groupName, taskName, result, kind);
        await SendWmsNotificationAsync(key, notification, content,
            groupName, taskName, result, kind, false, token);
    }

    public async Task SendWmsDocumentErrorsEmail(
        string groupName, string taskName, WmsDocumentRunResult result, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(result);
        token.ThrowIfCancellationRequested();
        var kind = WmsKind(result);
        if (!result.HasErrors)
            return;

        var key = kind == "KUF" ? "KufObrada.Errors" : "VlpObrada.Errors";
        if (!TryGetNotification(key, out var notification))
            return;

        var content = BuildWmsErrorsContent(groupName, taskName, result, kind);
        await SendWmsNotificationAsync(key, notification, content,
            groupName, taskName, result, kind, true, token);
    }

    private async Task SendWmsNotificationAsync(
        string key, TransferEmailConfiguration notification, string content,
        string groupName, string taskName, WmsDocumentRunResult result,
        string kind, bool errors, CancellationToken token)
    {
        var template = string.IsNullOrWhiteSpace(notification.BodyTemplate)
            ? "<html><body>{{Content}}</body></html>"
            : notification.BodyTemplate;
        // Prilagođen uvod ne sme sakriti rezultate ili greške.
        if (!template.Contains("{{Content}}", StringComparison.Ordinal))
        {
            var end = template.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (end < 0) end = template.IndexOf("</html>", StringComparison.OrdinalIgnoreCase);
            template = end < 0 ? template + "{{Content}}" : template.Insert(end, "{{Content}}");
        }

        // Jedan prolaz: tekst poruke/naziva sa {{...}} ne postaje novi placeholder.
        var body = Regex.Replace(template,
            @"\{\{(Content|Date|DateFrom|DateTo|GroupName|TaskName|TransferName|Vrsta|ImportId)\}\}",
            match => match.Groups[1].Value switch
            {
                "Content" => content,
                "Date" or "DateTo" => WmsHtml(result.DatumDo.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)),
                "DateFrom" => WmsHtml(result.DatumOd.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)),
                "GroupName" => WmsHtml(groupName),
                "TaskName" or "TransferName" => WmsHtml(taskName),
                "Vrsta" => WmsHtml(kind),
                "ImportId" => WmsHtml(result.ImportId),
                _ => match.Value
            });

        var subject = string.IsNullOrWhiteSpace(notification.Subject)
            ? errors ? $"ASW Transfer - greške u {kind} obradi" : $"ASW Transfer - {kind} kreirani nalozi"
            : notification.Subject;

        // Oblast u tabeli primalaca je isti pun ključ kao u Email.Notifications.
        // Ovde nema filtriranja kroz istoriju ponovljenih KIF neispravnih redova.
        if (!await SendEmail(subject, body, key, token))
            throw new InvalidOperationException(
                $"{kind} obaveštenje '{key}' nije poslato. Proverite uključeno slanje " +
                "i primaoce u CENTROSINERGIJA.dbo._tb_OblastMail " +
                $"(NazivAplikacije = Transfer, Oblast = {key}).");
    }

    private static string BuildWmsSuccessContent(
        string groupName, string taskName, WmsDocumentRunResult result, string kind)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"<h3>ASW Transfer - kreirani {kind} nalozi</h3>");
        AppendWmsRunDetails(builder, groupName, taskName, result, kind);

        if (result.HasErrors)
            builder.AppendLine("<p>Obrada ima i prijavljene probleme. Ovaj mail prikazuje kreirane naloge.</p>");
        AppendWmsIncompleteNotice(builder, result);

        var created = result.Journals
            .Where(x => x.Status == "Kreiran" && !string.IsNullOrWhiteSpace(x.AcKey))
            .ToList();
        var count = created.Select(x => x.AcKey).Distinct(StringComparer.Ordinal).Count();
        builder.AppendLine($"<p><b>Broj kreiranih naloga u primljenim rezultatima:</b> {count}</p>");
        builder.AppendLine("<table border='1' cellpadding='5' cellspacing='0'>");
        builder.AppendLine("<tr><th>Vrsta naloga (acDocType)</th>" +
            "<th>Nalog od</th><th>Nalog do</th><th>Broj kreiranih</th></tr>");
        foreach (var group in created.GroupBy(x => x.AcDocType, StringComparer.Ordinal)
                     .OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var keys = group.Select(x => x.AcKey).Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            builder.AppendLine($"<tr><td>{WmsHtml(group.Key)}</td><td>{WmsHtml(keys[0])}</td>" +
                $"<td>{WmsHtml(keys[^1])}</td><td>{keys.Length}</td></tr>");
        }
        builder.AppendLine("</table>");
        builder.AppendLine("<p>Od/do su najmanji i najveći ključ kreiran u ovom prolazu. " +
            "Opseg može imati prekide; poslednja kolona prikazuje stvarni broj naloga.</p>");
        return builder.ToString();
    }

    private static string BuildWmsErrorsContent(
        string groupName, string taskName, WmsDocumentRunResult result, string kind)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"<h3>ASW Transfer - greške u {kind} obradi</h3>");
        AppendWmsRunDetails(builder, groupName, taskName, result, kind);
        AppendWmsIncompleteNotice(builder, result);

        var errors = GetWmsEmailErrors(result);
        builder.AppendLine($"<p><b>Broj prijavljenih problema:</b> {errors.Count}</p>");
        builder.AppendLine("<table border='1' cellpadding='5' cellspacing='0'>");
        builder.AppendLine("<tr><th>Procedura</th><th>Faza</th><th>Izvorni nalog</th>" +
            "<th>Nalog</th><th>Vrsta (acDocType)</th><th>Broj greške</th><th>Linija</th><th>Poruka</th></tr>");
        foreach (var error in errors)
        {
            builder.AppendLine($"<tr><td>{WmsHtml(error.Procedura)}</td><td>{WmsHtml(error.Faza)}</td>" +
                $"<td>ID: {WmsHtml(error.NalogId)}<br>Vrsta: {WmsHtml(error.VrstaNaloga)}<br>" +
                $"Broj: {WmsHtml(error.Broj)}<br>Datum: {WmsHtml(error.Datum?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture))}</td>" +
                $"<td>{WmsHtml(error.AcKey)}</td><td>{WmsHtml(error.AcDocType)}</td>" +
                $"<td>{WmsHtml(error.BrojGreske)}</td><td>{WmsHtml(error.Linija)}</td>" +
                $"<td style='white-space:pre-wrap'>{WmsHtml(error.Poruka)}</td></tr>");
        }
        builder.AppendLine("</table>");
        return builder.ToString();
    }

    private static List<WmsProcessingError> GetWmsEmailErrors(WmsDocumentRunResult result)
    {
        var errors = new List<WmsProcessingError>(result.Errors);
        // Reader već dodaje ove probleme. Dopuna pokriva i rezultat bez takvog detalja.
        foreach (var journal in result.Journals.Where(x => x.HasBalanceDifference))
        {
            if (errors.Any(x => x.Faza == "Provera ravnoteže naloga"
                && x.NalogId == journal.NalogId && x.AcKey == journal.AcKey))
                continue;
            errors.Add(new WmsProcessingError
            {
                Procedura = WmsDocumentResultReader.ProcedureName,
                Faza = "Provera ravnoteže naloga",
                NalogId = journal.NalogId,
                VrstaNaloga = journal.VrstaNaloga,
                Broj = journal.Broj,
                Datum = journal.Datum,
                AcKey = journal.AcKey,
                AcDocType = journal.AcDocType,
                Poruka = FormattableString.Invariant(
                    $"Nalog ima razliku: duguje={journal.Duguje:0.####}; potražuje={journal.Potrazuje:0.####}; razlika={journal.Razlika:0.####}.")
            });
        }
        errors = errors.DistinctBy(x => (x.NalogId, x.VrstaNaloga, x.Broj, x.Datum,
            x.Procedura, x.Faza, x.AcKey, x.AcDocType, x.BrojGreske, x.Linija, x.Poruka)).ToList();
        if (errors.Count == 0)
            errors.Add(new WmsProcessingError
            {
                Procedura = WmsDocumentResultReader.ProcedureName,
                Faza = "Provera odgovora",
                Poruka = "Nedostaje kompletan odgovor procedure ili detalji problema prijavljenih u zbirnom rezultatu."
            });
        return errors;
    }

    private static void AppendWmsRunDetails(StringBuilder builder,
        string groupName, string taskName, WmsDocumentRunResult result, string kind)
    {
        var duration = TimeSpan.FromSeconds(Math.Floor(result.Duration.TotalSeconds));
        var preview = result.SamoProvera || result.Summary?.SamoProvera == true;
        builder.AppendLine($"<p><b>Grupa:</b> {WmsHtml(groupName)}<br>" +
            $"<b>Task:</b> {WmsHtml(taskName)}<br><b>Obrada:</b> {kind}<br>" +
            $"<b>Period:</b> {result.DatumOd:dd.MM.yyyy}–{result.DatumDo:dd.MM.yyyy}<br>" +
            $"<b>ImportId:</b> {WmsHtml(result.ImportId)}<br>" +
            $"<b>Trajanje:</b> {WmsHtml(duration.ToString("c", CultureInfo.InvariantCulture))}</p>");
        if (preview)
            builder.AppendLine("<p><b>Režim provere:</b> rezultati predstavljaju predlog; nalozi nisu upisani ovim pozivom.</p>");

        // Nepotpun zbirni odgovor ne predstavljamo kao konačne brojače.
        if (result.ResponseComplete && result.Summary is { } summary)
        {
            builder.AppendLine("<table border='1' cellpadding='5' cellspacing='0'>");
            builder.AppendLine($"<tr><th>Izabrano naloga</th><td>{summary.IzabranoNaloga}</td></tr>");
            builder.AppendLine($"<tr><th>Obrađeno naloga</th><td>{summary.ObradjenoNaloga}</td></tr>");
            builder.AppendLine($"<tr><th>Kreirano naloga</th><td>{summary.KreiranoNaloga}</td></tr>");
            builder.AppendLine($"<tr><th>Predloženo naloga</th><td>{summary.PredlozenoNaloga}</td></tr>");
            builder.AppendLine($"<tr><th>Preskočeno naloga</th><td>{summary.PreskocenoNaloga}</td></tr>");
            builder.AppendLine($"<tr><th>Greške obrade naloga</th><td>{summary.BrojGresaka}</td></tr>");
            builder.AppendLine($"<tr><th>Naloga sa razlikom</th><td>{summary.NalogaSaRazlikom}</td></tr>");
            builder.AppendLine("</table>");
        }
    }

    private static void AppendWmsIncompleteNotice(StringBuilder builder, WmsDocumentRunResult result)
    {
        if (result.ResponseComplete && result.Summary is not null)
            return;
        builder.AppendLine("<p><b>Završetak obrade nije potvrđen: odgovor nije kompletan.</b> " +
            "Prikazani su samo primljeni rezultati; konačan broj obrađenih naloga nije poznat.</p>");
        if (!result.SamoProvera && result.Summary?.SamoProvera != true)
            builder.AppendLine("<p>Ranije potvrđeni upisi mogu ostati u bazi. " +
                "Prekid odgovora ne znači da su svi upisi poništeni.</p>");
    }

    private static string WmsKind(WmsDocumentRunResult result) => result.Vrsta.Trim().ToUpperInvariant() switch
    {
        "KUF" => "KUF",
        "VLP" => "VLP",
        _ => throw new InvalidOperationException("WMS obaveštenje zahteva vrstu KUF ili VLP.")
    };

    private static string WmsHtml(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(text) ? "-" : HtmlEncoder.Default.Encode(text);
    }
}
