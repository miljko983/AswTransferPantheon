using System;
using System.Collections.Generic;
using System.Linq;

namespace AswTransferToPantheon.Infrastructure.Models;

public sealed class KifDocumentRunResult
{
    public DateTime Datum { get; set; }
    public bool SamoProvera { get; set; }

    public KifDocumentSummary? Summary { get; set; }

    public List<KifDocumentResult> Documents { get; } = [];
    public List<KifPostingResult> Postings { get; } = [];
    public List<KifProcessingError> Errors { get; } = [];

    public bool DocumentsResponseComplete { get; set; }
    public bool PostingResponseComplete { get; set; }

    public TimeSpan Duration { get; set; }

    public bool HasErrors =>
        !DocumentsResponseComplete
        || !PostingResponseComplete
        || Summary is null
        || (Summary?.Gresaka ?? 0) > 0
        || Errors.Count > 0
        || Documents.Any(x => x.Status == "Greska")
        || Postings.Any(x => x.Status == "Greska");

    public bool HasNewWork =>
        !SamoProvera
        && (
            Documents.Any(x => x.Status == "Kreiran")
            || Postings.Any(x => x.Status == "Knjizen")
        );
}

public sealed class KifDocumentSummary
{
    public bool SamoProvera { get; set; }
    public int IzabranoKIF { get; set; }
    public int Predlozeno { get; set; }
    public int Kreirano { get; set; }
    public int Gresaka { get; set; }
}

public sealed class KifDocumentResult
{
    public int KifId { get; set; }

    public string AcKey { get; set; } = "";
    public string AcDocType { get; set; } = "";

    // Kreiran, Preskocen, Predlog ili Greska.
    public string Status { get; set; } = "";

    public int BrojStavki { get; set; }
    public string Faza { get; set; } = "";
    public int? BrojGreske { get; set; }
    public string Poruka { get; set; } = "";
}

public sealed class KifPostingResult
{
    public string MoveKey { get; set; } = "";
    public DateTime Datum { get; set; }

    public string VrstaRoba { get; set; } = "";
    public string VrstaNaloga { get; set; } = "";
    public string Nalog { get; set; } = "";

    // Knjizen, Preskocen, Predlog ili Greska.
    public string Status { get; set; } = "";

    public int? BrojGreske { get; set; }
    public string Poruka { get; set; } = "";
}

public sealed class KifProcessingError
{
    public string Procedura { get; set; } = "";
    public string Faza { get; set; } = "";

    public int? KifId { get; set; }
    public string AcKey { get; set; } = "";
    public string AcDocType { get; set; } = "";

    public int? BrojGreske { get; set; }
    public int? Linija { get; set; }
    public string Poruka { get; set; } = "";
}