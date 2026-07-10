namespace AswTransferToPantheon.Services.Helpers;

public static class TransferErrorHelper
{
    public static bool IsCriticalError(Exception exception)
    {
        var text = exception.ToString();

        return text.Contains("Login failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Cannot open database", StringComparison.OrdinalIgnoreCase)
            || text.Contains("A network-related or instance-specific error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("server was not found", StringComparison.OrdinalIgnoreCase)
            || text.Contains("could not open a connection", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Login timeout expired", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ORA-01017", StringComparison.OrdinalIgnoreCase)
            || text.Contains("invalid username/password", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ORA-12154", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ORA-12514", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ORA-12541", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ORA-12545", StringComparison.OrdinalIgnoreCase)
            || text.Contains("TNS:", StringComparison.OrdinalIgnoreCase)
            || text.Contains("error occurred during the login process", StringComparison.OrdinalIgnoreCase)
            || text.Contains("during the login process", StringComparison.OrdinalIgnoreCase)
            || text.Contains("The login failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("authentication failed", StringComparison.OrdinalIgnoreCase);
    }
}