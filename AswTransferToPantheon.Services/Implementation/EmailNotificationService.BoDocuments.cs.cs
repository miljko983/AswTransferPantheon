using System;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using AswTransferToPantheon.Infrastructure.Models;

namespace AswTransferToPantheon.Services.Implementation;

public sealed partial class EmailNotificationService
{
    private readonly SemaphoreSlim boMailGate = new(1, 1);

    private readonly System.Collections.Generic.Dictionary<
        (string Group, string Task, DateTime Date),
        BoMailSnapshot?> boMailHistory = new();

    private sealed record BoMailSnapshot(
        string Nalog,
        int Selected,
        int Posted,
        decimal? Debit,
        decimal? Credit,
        decimal? Difference);

    public async Task SendBoDocumentRunEmail(
        string groupName,
        string taskName,
        BoDocumentRunResult result,
        CancellationToken token)
    {
        await boMailGate.WaitAsync(token);

        try
        {
            var historyKey = (groupName, taskName, result.Datum.Date);
            var snapshot = GetBoMailSnapshot(result);

            if (!result.SamoProvera)
            {
                var noNewDocuments = !result.HasErrors
                    && result.Documents.Count == 0
                    && result.Summary is
                    {
                        IzabranoDokumenata: 0,
                        KreiranoDokumenata: 0,
                        KreiranoStavki: 0,
                        BrojGresaka: 0,
                        PreostaloDokumenataZaDatum: 0,
                        GreskeKnjizenja: 0
                    };

                if (noNewDocuments && snapshot is not null)
                {
                    var hadPrevious = boMailHistory.TryGetValue(
                        historyKey, out var previous);

                    // Isti uspešan rezultat je već poslat.
                    if (hadPrevious && previous == snapshot)
                        return;

                    // Potpuno prazan datum, bez prethodnog problema.
                    if (!hadPrevious
                        && snapshot.Selected == 0
                        && snapshot.Posted == 0
                        && snapshot.Nalog.Length == 0)
                    {
                        return;
                    }
                }

                // Greška ili novi rezultat zahtevaju novo obaveštenje.
                // null ostaje i kada slanje bude preskočeno ili ne uspe.
                boMailHistory[historyKey] = null;
            }

            var key = result.HasErrors
                ? "BODokumenti.Errors"
                : "BODokumenti.Completed";

            if (!TryGetNotification(key, out var notification))
                return;

            var content = BuildBoDocumentContent(
                groupName, taskName, result);

            var template = string.IsNullOrWhiteSpace(notification.BodyTemplate)
                ? "<html><body>{{Content}}</body></html>"
                : notification.BodyTemplate;

            var body = template
                .Replace("{{Date}}",
                    HtmlEncoder.Default.Encode(result.Datum.ToString("dd.MM.yyyy")))
                .Replace("{{ImportId}}",
                    HtmlEncoder.Default.Encode(result.ImportId?.ToString() ?? "-"))
                .Replace("{{Status}}",
                    HtmlEncoder.Default.Encode(result.Status))
                .Replace("{{Content}}", content);

            var sent = await SendEmail(
                notification.Subject, body, key, token);

            if (sent && !result.SamoProvera && snapshot is not null)
                boMailHistory[historyKey] = snapshot;
        }
        finally
        {
            boMailGate.Release();
        }
    }

    private static BoMailSnapshot? GetBoMailSnapshot(
        BoDocumentRunResult result)
    {
        if (result.HasErrors
            || result.SamoProvera
            || result.Status != "Completed"
            || result.Summary is not
            {
                IzabranoZaKnjizenje: int selected,
                KnjizenoDokumenata: int posted
            } summary
            || selected < 0
            || posted != selected)
        {
            return null;
        }

        var nalog = summary.Nalog?.Trim() ?? "";

        if ((posted > 0 && nalog.Length == 0)
            || (nalog.Length > 0
                && (!summary.DugujeNaloga.HasValue
                    || !summary.PotrazujeNaloga.HasValue
                    || !summary.RazlikaZaIzravnanje.HasValue)))
        {
            return null;
        }

        return new BoMailSnapshot(
            nalog, selected, posted,
            summary.DugujeNaloga,
            summary.PotrazujeNaloga,
            summary.RazlikaZaIzravnanje);
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
                    ? "<h4>Zbirni pregled dokumenata koji su prošli proveru</h4>"
                    : "<h4>Kreirani dokumenti — pregled od–do</h4>");

            builder.AppendLine(
                "<table border='1' cellpadding='5' cellspacing='0'>" +
                "<tr>" +
                "<th>Vrsta dokumenta</th>" +
                "<th>Robni dokument od</th>" +
                "<th>Robni dokument do</th>" +
                "<th>Broj dokumenata</th>" +
                "<th>Vrsta naloga</th>" +
                "<th>Nalog</th>" +
                "</tr>");

            var groups = result.Documents
                .GroupBy(document => new
                {
                    VrstaDokumenta = document.VrstaDokumenta?.Trim() ?? "",
                    VrstaNaloga = document.VrstaNaloga?.Trim() ?? "",
                    Nalog = document.Nalog?.Trim() ?? ""
                })
                .OrderBy(group => group.Key.VrstaDokumenta, StringComparer.Ordinal)
                .ThenBy(group => group.Key.VrstaNaloga, StringComparer.Ordinal)
                .ThenBy(group => group.Key.Nalog, StringComparer.Ordinal);

            foreach (var group in groups)
            {
                var keys = group
                    .Select(document => document.RobniDokument?.Trim())
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Select(key => key!)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .ToArray();

                builder.AppendLine(
                    "<tr>" +
                    $"<td>{H(group.Key.VrstaDokumenta)}</td>" +
                    $"<td>{H(keys.FirstOrDefault())}</td>" +
                    $"<td>{H(keys.LastOrDefault())}</td>" +
                    $"<td>{H(group.Count())}</td>" +
                    $"<td>{H(group.Key.VrstaNaloga)}</td>" +
                    $"<td>{H(group.Key.Nalog)}</td>" +
                    "</tr>");
            }

            builder.AppendLine("</table>");

            if (!result.SamoProvera)
            {
                builder.AppendLine(
                    "<p>Od–do prikazuje prvi i poslednji robni dokument u grupi. " +
                    "Između njih mogu postojati praznine; broj dokumenata " +
                    "odnosi se na dokumente iz ovog uvoza.</p>");
            }
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
