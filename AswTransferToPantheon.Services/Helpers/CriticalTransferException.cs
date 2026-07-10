namespace AswTransferToPantheon.Services.Helpers;

public sealed class CriticalTransferException : Exception
{
    public CriticalTransferException(string message, Exception innerException) : base(message, innerException)
    {
    }
}