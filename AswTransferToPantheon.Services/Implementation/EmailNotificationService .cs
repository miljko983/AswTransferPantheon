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
        return SendEmail(configuration.Subject,
                BuildBody(
                    "Startup test",
                    "-",
                    "-",
                    "ASW Transfer aplikacija je pokrenuta.",
                    "-",
                    "-"),
                token);
        }

    public Task SendBadRecordsSummaryEmail(string groupName, string taskName, List<BadRecordInfo> badRecords, CancellationToken token)
    {
        if (!configuration.SendOnBadRecord || badRecords.Count == 0)
        {
            return Task.CompletedTask;
        }

        var subject = $"{configuration.Subject} - neispravni redovi - {taskName}";

        var body = BuildBadRecordsSummaryBody(groupName, taskName, badRecords);

        return SendEmail(subject, body, token);
    }

    public Task SendTaskErrorEmail(
        string groupName,
        string taskName,
        string message,
        Exception exception,
        CancellationToken token)
    {
        if (!configuration.SendOnTaskError)
        {
            return Task.CompletedTask;
        }

        var subject = $"{configuration.Subject} - greška taska - {taskName}";

        var body = BuildBody(
            taskName,
            "-",
            "-",
            message,
            exception.ToString(),
            "-");

        return SendEmail(subject, body, token);
    }

    private async Task SendEmail(string subject, string body, CancellationToken token)
    {
        if (!configuration.Enabled)
        {
            return;
        }

        if (configuration.To.Count == 0)
        {
            return;
        }

        using var mailMessage = new MailMessage();

        mailMessage.From = new MailAddress(configuration.From);

        foreach (var recipient in configuration.To.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            mailMessage.To.Add(recipient);
        }

        foreach (var cc in configuration.Cc.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            mailMessage.CC.Add(cc);
        }

        mailMessage.Subject = subject;
        mailMessage.Body = body;
        mailMessage.IsBodyHtml = true;

        using var smtpClient = new SmtpClient(configuration.SmtpHost, configuration.SmtpPort)
        {
            EnableSsl = configuration.EnableSsl,
            Credentials = new NetworkCredential(configuration.UserName, configuration.Password)
        };

        await smtpClient.SendMailAsync(mailMessage, token);
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
}