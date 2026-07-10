namespace AswTransferToPantheon.Infrastructure.Configuration;

public sealed class EmailConfiguration
{
    public bool Enabled { get; set; }

    public string SmtpHost { get; set; } = string.Empty;

    public int SmtpPort { get; set; } = 587;

    public bool EnableSsl { get; set; } = true;

    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string From { get; set; } = string.Empty;

    public List<string> To { get; set; } = [];

    public List<string> Cc { get; set; } = [];

    public string Subject { get; set; } = "ASW Transfer - greška u transferu";

    public string BodyTemplate { get; set; } = string.Empty;

    public bool SendOnBadRecord { get; set; } = true;

    public bool SendOnTaskError { get; set; } = true;
}