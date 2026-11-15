using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using AswTransferToPantheon.Infrastructure.Configuration;
using AswTransferToPantheon.Infrastructure.Models;
using AswTransferToPantheon.Services.Helpers;

namespace AswTransferToPantheon.Services.Implementation;

public sealed partial class EmailNotificationService
{
    public async Task SendKifDocumentSuccessEmail(
        string groupName, string taskName, KifDocumentRunResult result, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(result);
        token.ThrowIfCancellationRequested();
        if (!result.HasNewWork || result.SamoProvera)
            return;

        const string key = "KifObrada.Completed";
        if (!TryGetNotification(key, out var notification))
            return;

        var content = BuildKifSuccessContent(groupName, taskName, result);
        await SendKifNotificationAsync(key, notification, content, groupName, taskName, result, token);
    }

    public async Task SendKifDocumentErrorsEmail(
        string groupName, string taskName, KifDocumentRunResult result, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(result);
        token.ThrowIfCancellationRequested();
        if (!result.HasErrors)
            return;

        const string key = "KifObrada.Errors";
        if (!TryGetNotification(key, out var notification))
            return;

        var content = BuildKifErrorsContent(groupName, taskName, result);
        await SendKifNotificationAsync(key, notification, content, groupName, taskName, result, token);
    }

    private async Task SendKifNotificationAsync(
        string key, TransferEmailConfiguration notification, string content,
        string groupName, string taskName, KifDocumentRunResult result, CancellationToken token)
    {
        var template = string.IsNullOrWhiteSpace(notification.BodyTemplate)
            ? "<html><body>{{Content}}</body></html>"
            : notification.BodyTemplate;

        // Sačuvaj prilagođeni uvod, ali uvek uključi rezultate obrade.
        if (!template.Contains("{{Content}}", StringComparison.Ordinal))
        {
            var end = template.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (end < 0)
                end = template.IndexOf("</html>", StringComparison.OrdinalIgnoreCase);
            template = end >= 0
                ? template.Insert(end, "{{Content}}")
                : template + "{{Content}}";
        }

        var body = template
            .Replace("{{Date}}", KifHtml(result.Datum.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)))
            .Replace("{{GroupName}}", KifHtml(groupName))
            .Replace("{{TaskName}}", KifHtml(taskName))
            .Replace("{{TransferName}}", KifHtml(taskName))
            .Replace("{{Content}}", content);

        var subject = string.IsNullOrWhiteSpace(notification.Subject)
            ? key == "KifObrada.Errors"
                ? "ASW Transfer - greške u KIF obradi"
                : "ASW Transfer - KIF kreirani dokumenti i knjiženja"
            : notification.Subject;

        // Isti ključ se koristi u JSON-u i u tabeli mapiranja primalaca.
        var sent = await SendEmail(subject, body, key, token);
        if (!sent)
            throw new InvalidOperationException(
                $"KIF obaveštenje '{key}' nije poslato. Proverite uključeno slanje " +
                "i primaoce za ovu oblast u CENTROSINERGIJA.dbo._tb_OblastMail " +
                "(NazivAplikacije = Transfer).");
    }

    private static string BuildKifSuccessContent(
        string groupName, string taskName, KifDocumentRunResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<h3>ASW Transfer - uspešni rezultati KIF obrade</h3>");
        AppendKifRunDetails(builder, groupName, taskName, result);

        if (result.HasErrors)
            builder.AppendLine("<p>Ovaj pregled sadrži uspešne rezultate ovog prolaza. " +
                "Obrada ima i prijavljene probleme; oni su obuhvaćeni zasebnim obaveštenjem o greškama.</p>");

        if (!result.DocumentsResponseComplete || !result.PostingResponseComplete)
            builder.AppendLine("<p><b>Odgovor obrade nije kompletan.</b> Prikazani su samo " +
                "uspešni redovi primljeni od procedura. Pregled možda ne obuhvata sve upise.</p>");

        var created = result.Documents.Where(x => x.Status == "Kreiran").ToList();
        if (created.Count > 0)
        {
            builder.AppendLine("<h4>Kreirani su sledeći dokumenti</h4>");
            builder.AppendLine("<table border='1' cellpadding='5' cellspacing='0'>");
            builder.AppendLine("<tr><th>Vrsta dokumenta (acDocType)</th>" +
                "<th>Broj od</th><th>Broj do</th><th>Broj kreiranih</th></tr>");

            foreach (var group in created.GroupBy(x => x.AcDocType, StringComparer.Ordinal)
                         .OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var keys = group.Select(x => x.AcKey).Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal).ToArray();
                builder.AppendLine($"<tr><td>{KifHtml(group.Key)}</td>" +
                    $"<td>{KifHtml(keys[0])}</td><td>{KifHtml(keys[^1])}</td><td>{keys.Length}</td></tr>");
            }
            builder.AppendLine("</table>");
            builder.AppendLine("<p>Od/do prikazuje najmanji i najveći ključ kreiran u ovom prolazu. " +
                "Opseg može imati prekide; poslednja kolona prikazuje stvarni broj dokumenata.</p>");
        }

        var posted = result.Postings.Where(x => x.Status == "Knjizen")
            .Select(x => (x.VrstaRoba, x.VrstaNaloga, x.Nalog))
            .Distinct()
            .OrderBy(x => x.VrstaRoba, StringComparer.Ordinal)
            .ThenBy(x => x.VrstaNaloga, StringComparer.Ordinal)
            .ThenBy(x => x.Nalog, StringComparer.Ordinal)
            .ToList();

        if (posted.Count > 0)
        {
            builder.AppendLine("<h4>Dokumenti su proknjiženi na sledeće naloge</h4>");
            builder.AppendLine("<table border='1' cellpadding='5' cellspacing='0'>");
            builder.AppendLine("<tr><th>Vrsta robnog dokumenta</th><th>Vrsta naloga</th><th>Nalog</th></tr>");
            foreach (var item in posted)
                builder.AppendLine($"<tr><td>{KifHtml(item.VrstaRoba)}</td>" +
                    $"<td>{KifHtml(item.VrstaNaloga)}</td><td>{KifHtml(item.Nalog)}</td></tr>");
            builder.AppendLine("</table>");
        }
        return builder.ToString();
    }

    private static string BuildKifErrorsContent(
        string groupName, string taskName, KifDocumentRunResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<h3>ASW Transfer - greške u KIF obradi</h3>");
        AppendKifRunDetails(builder, groupName, taskName, result);

        if (result.SamoProvera)
            builder.AppendLine("<p><b>Režim provere:</b> rezultati predstavljaju proveru, " +
                "a ne stvarno kreiranje i knjiženje.</p>");

        if (!result.DocumentsResponseComplete || result.Summary is null)
            builder.AppendLine("<p><b>Kreiranje:</b> nije potvrđen kompletan odgovor procedure.</p>");
        if (!result.PostingResponseComplete)
            builder.AppendLine("<p><b>Knjiženje:</b> nije izvršeno ili nije potvrđen kompletan odgovor procedure.</p>");
        if (!result.SamoProvera && (!result.DocumentsResponseComplete || !result.PostingResponseComplete))
            builder.AppendLine("<p>Raniji potvrđeni upisi mogu ostati u bazi. " +
                "Iz nepotpunog odgovora ne može se utvrditi konačan broj svih upisa.</p>");

        var errors = GetKifEmailErrors(result);
        builder.AppendLine($"<p><b>Broj prijavljenih problema:</b> {errors.Count}</p>");
        builder.AppendLine("<table border='1' cellpadding='5' cellspacing='0'>");
        builder.AppendLine("<tr><th>Procedura</th><th>Faza</th><th>KIF ID</th>" +
            "<th>Dokument</th><th>Vrsta</th><th>Broj greške</th><th>Linija</th><th>Poruka</th></tr>");
        foreach (var error in errors)
            builder.AppendLine($"<tr><td>{KifHtml(error.Procedura)}</td>" +
                $"<td>{KifHtml(error.Faza)}</td><td>{KifHtml(error.KifId)}</td>" +
                $"<td>{KifHtml(error.AcKey)}</td><td>{KifHtml(error.AcDocType)}</td>" +
                $"<td>{KifHtml(error.BrojGreske)}</td><td>{KifHtml(error.Linija)}</td>" +
                $"<td style='white-space:pre-wrap'>{KifHtml(error.Poruka)}</td></tr>");
        builder.AppendLine("</table>");
        return builder.ToString();
    }

    private static List<KifProcessingError> GetKifEmailErrors(KifDocumentRunResult result)
    {
        var errors = new List<KifProcessingError>(result.Errors);
        errors.AddRange(result.Documents.Where(x => x.Status == "Greska")
            .Select(x => new KifProcessingError
            {
                Procedura = KifDocumentResultReader.DocumentsProcedure,
                Faza = x.Faza,
                KifId = x.KifId,
                AcKey = x.AcKey,
                AcDocType = x.AcDocType,
                BrojGreske = x.BrojGreske,
                Poruka = x.Poruka
            }));
        errors.AddRange(result.Postings.Where(x => x.Status == "Greska")
            .Select(x => new KifProcessingError
            {
                Procedura = KifDocumentResultReader.PostingProcedure,
                Faza = "Knjiženje KIF",
                AcKey = x.MoveKey,
                AcDocType = x.VrstaRoba,
                BrojGreske = x.BrojGreske,
                Poruka = x.Poruka
            }));

        errors = errors.DistinctBy(x => (
            x.Procedura, x.Faza, x.KifId, x.AcKey, x.AcDocType,
            x.BrojGreske, x.Linija, x.Poruka)).ToList();

        if (errors.Count == 0)
            errors.Add(new KifProcessingError
            {
                Faza = "Provera odgovora",
                Poruka = "Obrada nema potvrđen uspešan završetak. " +
                    "Nedostaje kompletan odgovor ili detalji grešaka prijavljenih u zbirnom rezultatu."
            });
        return errors;
    }

    private static void AppendKifRunDetails(
        StringBuilder builder, string groupName, string taskName, KifDocumentRunResult result)
    {
        var duration = TimeSpan.FromSeconds(Math.Floor(result.Duration.TotalSeconds));
        builder.AppendLine($"<p><b>Grupa:</b> {KifHtml(groupName)}<br>" +
            $"<b>Task:</b> {KifHtml(taskName)}<br>" +
            $"<b>Datum obrade:</b> {result.Datum:dd.MM.yyyy}<br>" +
            $"<b>Trajanje:</b> {KifHtml(duration.ToString("c", CultureInfo.InvariantCulture))}</p>");
    }

    private static string KifHtml(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(text) ? "-" : HtmlEncoder.Default.Encode(text);
    }
}
