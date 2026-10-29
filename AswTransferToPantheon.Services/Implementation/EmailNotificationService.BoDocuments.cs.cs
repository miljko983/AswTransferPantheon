using System;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using AswTransferToPantheon.Infrastructure.Models;

namespace AswTransferToPantheon.Services.Implementation;

public sealed partial class EmailNotificationService
{
    public Task SendBoDocumentRunEmail(
        string groupName,
        string taskName,
        BoDocumentRunResult result,
        CancellationToken token)
    {
        var key = result.HasErrors
            ? "BODokumenti.Errors"
            : "BODokumenti.Completed";

        if (!TryGetNotification(key, out var notification))
            return Task.CompletedTask;

        var content = BuildBoDocumentContent(
            groupName,
            taskName,
            result);

        var template = string.IsNullOrWhiteSpace(notification.BodyTemplate)
            ? "<html><body>{{Content}}</body></html>"
            : notification.BodyTemplate;

        var body = template
            .Replace(
                "{{Date}}",
                HtmlEncoder.Default.Encode(
                    result.Datum.ToString("dd.MM.yyyy")))
            .Replace(
                "{{ImportId}}",
                HtmlEncoder.Default.Encode(
                    result.ImportId?.ToString() ?? "-"))
            .Replace(
                "{{Status}}",
                HtmlEncoder.Default.Encode(result.Status))
            .Replace("{{Content}}", content);

        return SendEmail(notification.Subject, body, key, token);
    }

    private static string BuildBoDocumentContent(
        string groupName,
        string taskName,
        BoDocumentRunResult result)
    {
        static string H(object? value)
        {
            var text = value?.ToString();

            return string.IsNullOrWhiteSpace(text)
                ? "-"
                : HtmlEncoder.Default.Encode(text);
        }

        var builder = new StringBuilder();

        var title = result.HasErrors
            ? "BO obrada sa prijavljenim problemima"
            : result.SamoProvera
                ? "BO provera završena"
                : "BO uvoz završen";

        builder.AppendLine(
            $"<h3>ASW Transfer - {H(title)}</h3>");

        var duration = TimeSpan.FromSeconds(
            Math.Floor(result.Duration.TotalSeconds));

        builder.AppendLine(
            $"<p><b>Grupa:</b> {H(groupName)}<br>" +
            $"<b>Task:</b> {H(taskName)}<br>" +
            $"<b>Datum obrade:</b> {result.Datum:dd.MM.yyyy}<br>" +
            $"<b>ImportId:</b> {H(result.ImportId)}<br>" +
            $"<b>Status:</b> {H(result.Status)}<br>" +
            $"<b>Trajanje:</b> {H(duration.ToString("c"))}</p>");

        if (result.SamoProvera)
        {
            builder.AppendLine(
                "<p>Uključen je režim provere bez kreiranja dokumenata.</p>");
        }

        if (!result.ResponseComplete)
        {
            builder.AppendLine(
                "<p>Kompletan odgovor procedure nije potvrđen. " +
                "Prikazani su dostupni podaci.</p>");
        }

        if (result.RecoveredFromLedger)
        {
            builder.AppendLine(
                "<p>Poziv je prijavio grešku. Sačuvani rezultati ispod " +
                "pročitani su iz trajne evidencije ovog ImportId.</p>");
        }

        if (result.Summary is { } summary)
        {
            builder.AppendLine(
                "<table border='1' cellpadding='5' cellspacing='0'>");

            void Row(string label, object? value) =>
                builder.AppendLine(
                    $"<tr><th>{H(label)}</th><td>{H(value)}</td></tr>");

            Row("Izabrano dokumenata", summary.IzabranoDokumenata);
            Row("Kreirano dokumenata", summary.KreiranoDokumenata);
            Row("Kreirano stavki", summary.KreiranoStavki);

            Row(
                "Nalog",
                string.IsNullOrWhiteSpace(summary.Nalog)
                    ? "-"
                    : summary.Nalog);

            Row(
                "Preostalo dokumenata za datum",
                summary.PreostaloDokumenataZaDatum);

            Row("Izabrano za knjiženje", summary.IzabranoZaKnjizenje);
            Row("Knjiženo dokumenata", summary.KnjizenoDokumenata);
            Row("Greške knjiženja", summary.GreskeKnjizenja);
            Row("Ukupno grešaka", summary.BrojGresaka);

            Row("Duguje naloga", summary.DugujeNaloga);
            Row("Potražuje naloga", summary.PotrazujeNaloga);
            Row("Razlika za izravnanje", summary.RazlikaZaIzravnanje);

            builder.AppendLine("</table>");
        }
        else
        {
            builder.AppendLine(
                "<p>Nije dobijen zbirni rezultat. " +
                "Broj sačuvanih dokumenata nije potvrđen.</p>");
        }

        if (result.ExecutionException is { } exception)
        {
            builder.AppendLine(
                $"<h4>Greška poziva</h4>" +
                $"<pre>{H(exception.ToString())}</pre>");

            if (!result.SamoProvera)
            {
                builder.AppendLine(
                    "<p>Prekid poziva ne poništava ranije potvrđene " +
                    "pakete. Proverite evidenciju uvoza pre ponovnog " +
                    "pokretanja.</p>");
            }
        }

        if (!string.IsNullOrWhiteSpace(result.DiagnosticsError))
        {
            builder.AppendLine(
                $"<p><b>Čitanje evidencije:</b> " +
                $"{H(result.DiagnosticsError)}</p>");
        }

        if (result.Documents.Count > 0)
        {
            builder.AppendLine(
                result.SamoProvera
                    ? "<h4>Dokumenti koji su prošli proveru</h4>"
                    : "<h4>Kreirani dokumenti u ovom uvozu</h4>");

            builder.AppendLine(
                "<table border='1' cellpadding='5' cellspacing='0'>" +
                "<tr>" +
                "<th>Dokument</th>" +
                "<th>Datum</th>" +
                "<th>Vrsta</th>" +
                "<th>Robni dokument</th>" +
                "<th>Vrsta naloga</th>" +
                "<th>Nalog</th>" +
                "<th>Stavki</th>" +
                "</tr>");

            foreach (var document in result.Documents)
            {
                builder.AppendLine(
                    "<tr>" +
                    $"<td>{H(document.Dokument)}</td>" +
                    $"<td>{H(document.Datum?.ToString("dd.MM.yyyy"))}</td>" +
                    $"<td>{H(document.VrstaDokumenta)}</td>" +
                    $"<td>{H(document.RobniDokument)}</td>" +
                    $"<td>{H(document.VrstaNaloga)}</td>" +
                    $"<td>{H(document.Nalog)}</td>" +
                    $"<td>{H(document.BrojStavki)}</td>" +
                    "</tr>");
            }

            builder.AppendLine("</table>");
        }

        if (result.Errors.Count > 0)
        {
            builder.AppendLine(
                "<h4>Greške po dokumentima i fazama</h4>");

            builder.AppendLine(
                "<table border='1' cellpadding='5' cellspacing='0'>" +
                "<tr>" +
                "<th>Dokument</th>" +
                "<th>Izvorni broj</th>" +
                "<th>Ident</th>" +
                "<th>Faza</th>" +
                "<th>Broj greške</th>" +
                "<th>Poruka</th>" +
                "<th>Procedura / linija</th>" +
                "</tr>");

            foreach (var error in result.Errors)
            {
                builder.AppendLine(
                    "<tr>" +
                    $"<td>{H(error.Dokument)}</td>" +
                    $"<td>{H(error.IzvorniBroj)}</td>" +
                    $"<td>{H(error.Ident)}</td>" +
                    $"<td>{H(error.Faza)}</td>" +
                    $"<td>{H(error.BrojGreske)}</td>" +
                    $"<td>{H(error.Poruka)}</td>" +
                    $"<td>{H(error.Procedura)} / {H(error.Linija)}</td>" +
                    "</tr>");
            }

            builder.AppendLine("</table>");
        }

        return builder.ToString();
    }
}