namespace AswTransferToPantheon.Infrastructure.Models;

public sealed class CreatedDocumentInfo
{
    public string AcDocType { get; set; } = string.Empty;

    public string DocumentName { get; set; } = string.Empty;

    public string NumberFrom { get; set; } = string.Empty;

    public string NumberTo { get; set; } = string.Empty;
}