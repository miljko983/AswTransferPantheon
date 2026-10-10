using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AswTransferToPantheon.Services.Implementation;

public sealed class EmailRecipientRefreshService : BackgroundService
{
    private readonly EmailRecipientCache emailRecipientCache;
    private readonly ILogger<EmailRecipientRefreshService> logger;

    public EmailRecipientRefreshService(
        EmailRecipientCache emailRecipientCache,
        ILogger<EmailRecipientRefreshService> logger)
    {
        this.emailRecipientCache = emailRecipientCache;
        this.logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshSafely(cancellationToken);

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.Now;

            var nextRefresh =
                now.Date.AddHours(6);

            if (nextRefresh <= now)
            {
                nextRefresh = nextRefresh.AddDays(1);
            }

            var delay = nextRefresh - now;

            await Task.Delay(
                delay,
                stoppingToken);

            await RefreshSafely(stoppingToken);
        }
    }

    private async Task RefreshSafely(
        CancellationToken token)
    {
        try
        {
            await emailRecipientCache.RefreshAsync(token);

            logger.LogInformation("Primaoci za email obaveštenja su osveženi.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Greška pri učitavanju primalaca email obaveštenja.");
        }
    }
}