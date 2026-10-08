using AswTransferToPantheon.Infrastructure.Configuration;
using AswTransferToPantheon.Infrastructure.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System.Data;
using System.Net.Mail;

namespace AswTransferToPantheon.Services.Implementation;

public sealed class EmailRecipientCache
{
    private readonly ConnectionStrings connectionStrings;
    private readonly object sync = new();

    private Dictionary<string, EmailRecipients> recipientsByArea =
        new(StringComparer.OrdinalIgnoreCase);

    public EmailRecipientCache(
        IOptions<ConnectionStrings> connectionStrings)
    {
        this.connectionStrings = connectionStrings.Value;
    }

    public async Task RefreshAsync(CancellationToken token)
    {
        var loadedRecipients = new Dictionary<string, EmailRecipients>(StringComparer.OrdinalIgnoreCase);

        await using var connection = new SqlConnection(connectionStrings.Transfer);

        await connection.OpenAsync(token);

        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT
                LTRIM(RTRIM(Mail)) AS Mail,
                LTRIM(RTRIM(Oblast)) AS Oblast
            FROM CENTROSINERGIJA.dbo._tb_OblastMail
            WHERE LTRIM(RTRIM(NazivAplikacije)) = @NazivAplikacije
              AND NULLIF(LTRIM(RTRIM(Mail)), '') IS NOT NULL
              AND NULLIF(LTRIM(RTRIM(Oblast)), '') IS NOT NULL;
            """;

        command.CommandType = CommandType.Text;

        command.Parameters.Add("@NazivAplikacije", SqlDbType.VarChar, 100).Value = "Transfer";

        await using var reader = await command.ExecuteReaderAsync(token);

        while (await reader.ReadAsync(token))
        {
            var mail = reader["Mail"]?.ToString();
            var oblast = reader["Oblast"]?.ToString();

            if (string.IsNullOrWhiteSpace(mail) ||
                string.IsNullOrWhiteSpace(oblast))
            {
                continue;
            }

            if (!loadedRecipients.TryGetValue(oblast, out var recipients))
            {
                recipients = new EmailRecipients();

                loadedRecipients[oblast] = recipients;
            }

            var mailovi = mail
                        .Split(
                            ';',
                            StringSplitOptions.RemoveEmptyEntries |
                            StringSplitOptions.TrimEntries)
                        .Where(IsValidEmail)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

            if (mailovi.Count == 0)
            {
                continue;
            }

            if (!recipients.To.Contains(mailovi[0], StringComparer.OrdinalIgnoreCase))
            {
                recipients.To.Add(mailovi[0]);
            }

            foreach (var ccMail in mailovi.Skip(1))
            {
                if (!recipients.Cc.Contains(ccMail, StringComparer.OrdinalIgnoreCase))
                {
                    recipients.Cc.Add(ccMail);
                }
            }
        }

        foreach (var recipients in loadedRecipients.Values)
        {
            recipients.Cc.RemoveAll(cc => recipients.To.Contains(cc, StringComparer.OrdinalIgnoreCase));
        }

        lock (sync)
        {
            recipientsByArea = loadedRecipients;
        }
    }

    public EmailRecipients? GetRecipients(string oblast)
    {
        lock (sync)
        {
            return recipientsByArea.TryGetValue(
                oblast,
                out var recipients)
                    ? recipients
                    : null;
        }
    }

    private static bool IsValidEmail(string email)
    {
        try
        {
            var mailAddress = new MailAddress(email);

            return mailAddress.Address.Equals(
                       email,
                       StringComparison.OrdinalIgnoreCase)
                   && email.Contains('@')
                   && email.IndexOf('@') ==
                      email.LastIndexOf('@');
        }
        catch
        {
            return false;
        }
    }
}