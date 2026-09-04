using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;

namespace AswTransferToPantheon.Services.Helpers;

public static class TransferErrorHelper
{
    public static bool IsCriticalError(Exception exception)
    {
        for (
            Exception? current = exception;
            current is not null;
            current = current.InnerException)
        {
            if (current is SqlException sqlException)
            {
                if (sqlException.Number is
                    -2 or      // SQL timeout
                    53 or      // server nije pronađen
                    64 or      // network error
                    233 or     // konekcija nije dostupna
                    258 or     // timeout
                    4060 or    // baza ne može da se otvori
                    10054 or   // konekcija prekinuta
                    10060)     // connection timeout
                {
                    return true;
                }
            }

            if (current is OracleException oracleException)
            {
                if (oracleException.Number is
                    1017 or    // pogrešan user/password
                    12154 or   // TNS problem
                    12514 or   // listener ne prepoznaje servis
                    12541 or   // listener nije dostupan
                    12545)     // host nije dostupan
                {
                    return true;
                }
            }
        }

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