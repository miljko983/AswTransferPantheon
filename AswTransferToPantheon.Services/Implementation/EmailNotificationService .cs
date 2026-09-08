using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Encodings.Web;
using AswTransferToPantheon.Infrastructure.Configuration;
using AswTransferToPantheon.Infrastructure.Models;
using AswTransferToPantheon.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace AswTransferToPantheon.Services.Implementation;

public sealed class EmailNotificationService : IEmailNotificationService
{
    private readonly EmailConfiguration configuration;

    public EmailNotificationService(IOptions<EmailConfiguration> configuration)
    {
        this.configuration = configuration.Value;
    }

    public Task SendTestEmail(CancellationToken token)
    {
        if (!TryGetNotification(
                "Startup.Test",
                out var notification))
        {
            return Task.CompletedTask;
        }

        var body = BuildBody(
            "Startup test",
            "-",
            "-",
            "ASW Transfer aplikacija je pokrenuta.",
            "-",
            "-");

        return SendEmail(
            notification.Subject,
            body,
            notification.To,
            notification.Cc,
            token);
    }

    public Task SendBadRecordsSummaryEmail(string groupName, string taskName, List<BadRecordInfo> badRecords, CancellationToken token)
    {
        if (badRecords.Count == 0)
        {
            return Task.CompletedTask;
        }

        var notificationKey =
            $"{taskName}.BadRecords";

        if (!TryGetNotification(
                notificationKey,
                out var notification))
        {
            return Task.CompletedTask;
        }

        var subject = notification.Subject;

        var body = BuildBadRecordsSummaryBody(
            groupName,
            taskName,
            badRecords);

        return SendEmail(
            subject,
            body,
            notification.To,
            notification.Cc,
            token);
    }

    public Task SendTaskErrorEmail(string groupName, string taskName, string message, Exception exception, CancellationToken token, string notificationKey = "Task.Error")
    {
        if (!TryGetNotification(
                notificationKey,
                out var notification))
        {
            return Task.CompletedTask;
        }

        var body = BuildBody(
            taskName,
            "-",
            "-",
            message,
            exception.ToString(),
            "-");

        return SendEmail(
            notification.Subject,
            body,
            notification.To,
            notification.Cc,
            token);
    }

    private async Task SendEmail(string subject, string body, List<string> recipients, List<string> ccRecipients,
    CancellationToken token)
    {
        if (!configuration.Enabled ||
            recipients.Count == 0)
        {
            return;
        }

        using var mailMessage = new MailMessage
        {
            From = new MailAddress(configuration.From),
            Subject = subject,
            Body = body,
            IsBodyHtml = true
        };

        foreach (var recipient in recipients
            .Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            mailMessage.To.Add(recipient);
        }

        foreach (var cc in ccRecipients
            .Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            mailMessage.CC.Add(cc);
        }

        using var smtpClient = new SmtpClient(
            configuration.SmtpHost,
            configuration.SmtpPort)
        {
            EnableSsl = configuration.EnableSsl,
            Credentials = new NetworkCredential(
                configuration.UserName,
                configuration.Password)
        };

        await smtpClient.SendMailAsync(
            mailMessage,
            token);
    }

    private string BuildBadRecordsSummaryBody(string groupName, string taskName, List<BadRecordInfo> badRecords)
    {
        var builder = new StringBuilder();

        builder.AppendLine("<html><body>");
        builder.AppendLine("<h3>ASW Transfer - neispravni redovi</h3>");
        builder.AppendLine($"<p><b>Grupa:</b> {HtmlEncoder.Default.Encode(groupName)}</p>");
        builder.AppendLine($"<p><b>Task:</b> {HtmlEncoder.Default.Encode(taskName)}</p>");
        builder.AppendLine($"<p><b>Broj neispravnih redova:</b> {badRecords.Count}</p>");

        builder.AppendLine("<table border='1' cellpadding='5' cellspacing='0'>");
        builder.AppendLine("<tr>");
        builder.AppendLine("<th>Tabela</th>");
        builder.AppendLine("<th>Ključ</th>");
        builder.AppendLine("<th>Poruka</th>");
        builder.AppendLine("<th>Exception</th>");
        builder.AppendLine("</tr>");

        foreach (var badRecord in badRecords)
        {
            builder.AppendLine("<tr>");
            builder.AppendLine($"<td>{HtmlEncoder.Default.Encode(badRecord.TableName)}</td>");
            builder.AppendLine($"<td>{HtmlEncoder.Default.Encode(badRecord.Key)}</td>");
            builder.AppendLine($"<td>{HtmlEncoder.Default.Encode(badRecord.Message)}</td>");
            builder.AppendLine($"<td>{HtmlEncoder.Default.Encode(badRecord.Exception)}</td>");
            builder.AppendLine("</tr>");
        }

        builder.AppendLine("</table>");
        builder.AppendLine("</body></html>");

        return builder.ToString();
    }
    private string BuildBody(
        string transferName,
        string tableName,
        string key,
        string message,
        string exception,
        string data)
    {
        var template = configuration.BodyTemplate;

        if (string.IsNullOrWhiteSpace(template))
        {
            template = """
                <html>
                    <body>
                        <h3>ASW Transfer obaveštenje</h3>
                        <p><b>Transfer:</b> {{TransferName}}</p>
                        <p><b>Tabela:</b> {{TableName}}</p>
                        <p><b>Ključ:</b> {{Key}}</p>
                        <p><b>Poruka:</b> {{Message}}</p>
                        <h4>Podaci</h4>
                        <pre>{{Data}}</pre>
                        <h4>Exception</h4>
                        <pre>{{Exception}}</pre>
                    </body>
                </html>
                """;
        }

        return template
            .Replace("{{TransferName}}", HtmlEncoder.Default.Encode(transferName))
            .Replace("{{TableName}}", HtmlEncoder.Default.Encode(tableName))
            .Replace("{{Key}}", HtmlEncoder.Default.Encode(key))
            .Replace("{{Message}}", HtmlEncoder.Default.Encode(message))
            .Replace("{{Data}}", HtmlEncoder.Default.Encode(data))
            .Replace("{{Exception}}", HtmlEncoder.Default.Encode(exception));
    }

    public Task SendCreatedArticlesSummaryEmail(
    string groupName,
    string taskName,
    List<CreatedArticleInfo> articles,
    CancellationToken token)
    {
        if (articles.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (!TryGetNotification(
                "Artikli.Created",
                out var notification))
        {
            return Task.CompletedTask;
        }

        var subject = notification.Subject;

        var body = BuildCreatedArticlesSummaryBody(groupName, taskName, articles);

        return SendEmail(subject, body, notification.To, notification.Cc, token);
    }

    private bool TryGetNotification(
    string notificationKey,
    out TransferEmailConfiguration notification)
    {
        notification = null!;

        if (!configuration.Enabled)
        {
            return false;
        }

        if (!configuration.Notifications.TryGetValue(
                notificationKey,
                out var foundNotification))
        {
            return false;
        }

        if (!foundNotification.Enabled ||
            foundNotification.To.Count == 0)
        {
            return false;
        }

        notification = foundNotification;
        return true;
    }
    private string BuildCreatedArticlesSummaryBody(string groupName, string taskName, List<CreatedArticleInfo> articles)
    {
        var builder = new StringBuilder();

        builder.AppendLine("<html><body>");
        builder.AppendLine("<h3>Kreirani su sledeći artikli u CL_WMS bazi:</h3>");

        builder.AppendLine(
            $"<p><b>Transfer:</b> " +
            $"{HtmlEncoder.Default.Encode(taskName)}</p>");

        builder.AppendLine(
            $"<p><b>Ukupno:</b> {articles.Count}</p>");

        builder.AppendLine(
            "<table border='1' cellpadding='5' cellspacing='0'>");

        builder.AppendLine(
            "<tr><th>Šifra</th><th>Naziv</th><th>Vrsta</th></tr>");

        foreach (var article in articles)
        {
            builder.AppendLine("<tr>");

            builder.AppendLine(
                $"<td>{HtmlEncoder.Default.Encode(article.AcIdent)}</td>");

            builder.AppendLine(
                $"<td>{HtmlEncoder.Default.Encode(article.AcName)}</td>");

            builder.AppendLine(
                $"<td>{HtmlEncoder.Default.Encode(article.AcClassif)}</td>");

            builder.AppendLine("</tr>");
        }

        builder.AppendLine("</table>");
        builder.AppendLine("</body></html>");

        return builder.ToString();
    }

    public Task SendCreatedDocumentsSummaryEmail(string groupName, string taskName, List<CreatedDocumentInfo> documents, CancellationToken token)
    {
        if (documents.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (!TryGetNotification("Dokumenti.Created", out var notification))
        {
            return Task.CompletedTask;
        }

        var subject = notification.Subject;

        var body = BuildCreatedDocumentsSummaryBody(groupName, taskName, documents);

        return SendEmail(subject, body, notification.To, notification.Cc, token);
    }

    private string BuildCreatedDocumentsSummaryBody(string groupName, string taskName, List<CreatedDocumentInfo> documents)
    {
        var builder = new StringBuilder();

        builder.AppendLine("<html><body>");
        builder.AppendLine("<h3>Kreirani su sledeći dokumenti:</h3>");

        builder.AppendLine($"<p><b>Transfer:</b> " + $"{HtmlEncoder.Default.Encode(taskName)}</p>");

        builder.AppendLine($"<p><b>Broj vrsta dokumenata:</b> " + $"{documents.Count}</p>");

        builder.AppendLine("<table border='1' cellpadding='5' cellspacing='0'>");

        builder.AppendLine(
            "<tr>" +
            "<th>Vrsta dokumenta</th>" +
            "<th>Naziv vrste</th>" +
            "<th>Broj od</th>" +
            "<th>Broj do</th>" +
            "</tr>");

        foreach (var document in documents)
        {
            builder.AppendLine("<tr>");

            builder.AppendLine($"<td>{HtmlEncoder.Default.Encode(document.AcDocType)}</td>");

            builder.AppendLine($"<td>{HtmlEncoder.Default.Encode(document.DocumentName)}</td>");

            builder.AppendLine($"<td>{document.NumberFrom}</td>");

            builder.AppendLine($"<td>{document.NumberTo}</td>");

            builder.AppendLine("</tr>");
        }

        builder.AppendLine("</table>");
        builder.AppendLine("</body></html>");

        return builder.ToString();
    }

    public Task SendDocumentCreationErrorsSummaryEmail(string groupName, string taskName, List<BadRecordInfo> errors, CancellationToken token)
    {
        if (errors.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (!TryGetNotification("Dokumenti.CreationErrors", out var notification))
        {
            return Task.CompletedTask;
        }

        var body = BuildBadRecordsSummaryBody(groupName, taskName, errors);

        return SendEmail(notification.Subject, body, notification.To, notification.Cc, token);
    }

    public Task SendCreatedIdentiCentrosinergijaSummaryEmail(string groupName, string taskName, List<CreatedIdentCentrosinergijaInfo> identi, CancellationToken token)
    {
        if (identi.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (!TryGetNotification("KreiranjeIdenata.Created", out var notification))
        {
            return Task.CompletedTask;
        }

        var body = BuildCreatedIdentiCentrosinergijaSummaryBody(groupName, taskName, identi);

        return SendEmail(notification.Subject, body, notification.To, notification.Cc, token);
    }

    private string BuildCreatedIdentiCentrosinergijaSummaryBody(string groupName, string taskName, List<CreatedIdentCentrosinergijaInfo> identi)
    {
        var builder = new StringBuilder();

        builder.AppendLine("<html><body>");

        builder.AppendLine(
            "<h3>Kreirani su sledeći identi u CENTROSINERGIJA bazi:</h3>");

        builder.AppendLine(
            $"<p><b>Transfer:</b> " +
            $"{HtmlEncoder.Default.Encode(taskName)}</p>");

        builder.AppendLine(
            $"<p><b>Ukupno kreiranih identa:</b> {identi.Count}</p>");

        builder.AppendLine(
            "<table border='1' cellpadding='5' cellspacing='0'>");

        builder.AppendLine(
            "<tr>" +
            "<th>Šifra identa</th>" +
            "<th>ID ASW</th>" +
            "<th>BAT šifra</th>" +
            "<th>Dobavljač</th>" +
            "<th>Naziv dobavljača</th>" +
            "<th>Grupa</th>" +
            "<th>Podgrupa</th>" +
            "<th>Vrsta</th>" +
            "<th>Odeljenje</th>" +
            "<th>Nosioc troška</th>" +
            "<th>Konto NV</th>" +
            "<th>Konto prihod</th>" +
            "<th>Datum kreiranja</th>" +
            "</tr>");

        foreach (var ident in identi)
        {
            builder.AppendLine("<tr>");

            builder.AppendLine(
                $"<td>{HtmlEncoder.Default.Encode(ident.SifraIdenta)}</td>");

            builder.AppendLine(
                $"<td>{HtmlEncoder.Default.Encode(ident.IdAsw)}</td>");

            builder.AppendLine(
                $"<td>{HtmlEncoder.Default.Encode(ident.BatSifra)}</td>");

            builder.AppendLine(
                $"<td>{HtmlEncoder.Default.Encode(ident.Dobavljac)}</td>");

            builder.AppendLine(
                $"<td>{HtmlEncoder.Default.Encode(ident.NazivDobavljaca)}</td>");

            builder.AppendLine(
                $"<td>{HtmlEncoder.Default.Encode(ident.Grupa)}</td>");

            builder.AppendLine(
                $"<td>{HtmlEncoder.Default.Encode(ident.Podgrupa)}</td>");

            builder.AppendLine(
                $"<td>{HtmlEncoder.Default.Encode(ident.Vrsta)}</td>");

            builder.AppendLine(
                $"<td>{HtmlEncoder.Default.Encode(ident.Odeljenje)}</td>");

            builder.AppendLine(
                $"<td>{HtmlEncoder.Default.Encode(ident.NosiocTroska)}</td>");

            builder.AppendLine(
                $"<td>{HtmlEncoder.Default.Encode(ident.KontoNv)}</td>");

            builder.AppendLine(
                $"<td>{HtmlEncoder.Default.Encode(ident.KontoPrihod)}</td>");

            builder.AppendLine(
                $"<td>{ident.DatumKreiranja:dd.MM.yyyy HH:mm:ss}</td>");

            builder.AppendLine("</tr>");
        }

        builder.AppendLine("</table>");
        builder.AppendLine("</body></html>");

        return builder.ToString();
    }
}