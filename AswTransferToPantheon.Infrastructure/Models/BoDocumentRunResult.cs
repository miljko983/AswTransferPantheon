using System;
using System.Collections.Generic;

namespace AswTransferToPantheon.Infrastructure.Models;

public sealed class BoDocumentRunResult
{
    public DateTime Datum { get; set; }
    public bool SamoProvera { get; set; }

    public long? ImportId { get; set; }
    public string Status { get; set; } = "Unknown";

    public BoDocumentSummary? Summary { get; set; }

    public List<BoImportedDocument> Documents { get; } = [];
    public List<BoDocumentError> Errors { get; } = [];

    public Exception? ExecutionException { get; set; }
    public string DiagnosticsError { get; set; } = "";

    public bool RecoveredFromLedger { get; set; }
    public bool ResponseComplete { get; set; }

    public TimeSpan Duration { get; set; }

    public bool HasErrors =>
        ExecutionException != null
        || Errors.Count > 0
        || Summary is null
        || (Summary?.BrojGresaka ?? 0) > 0
        || (Summary?.GreskeKnjizenja ?? 0) > 0
        || (Summary?.PreostaloDokumenataZaDatum ?? 0) > 0
        || !ResponseComplete
        || Status is not ("Completed" or "Validated");
}

public sealed class BoDocumentSummary
{
    public int? IzabranoDokumenata { get; set; }
    public int? KreiranoDokumenata { get; set; }
    public int? KreiranoStavki { get; set; }
    public int? BrojGresaka { get; set; }

    public string Nalog { get; set; } = "";

    public int? PreostaloDokumenataZaDatum { get; set; }

    public int? IzabranoZaKnjizenje { get; set; }
    public int? KnjizenoDokumenata { get; set; }
    public int? GreskeKnjizenja { get; set; }

    public decimal? DugujeNaloga { get; set; }
    public decimal? PotrazujeNaloga { get; set; }
    public decimal? RazlikaZaIzravnanje { get; set; }
}

public sealed class BoImportedDocument
{
    public string Dokument { get; set; } = "";
    public DateTime? Datum { get; set; }

    public string VrstaDokumenta { get; set; } = "";
    public string RobniDokument { get; set; } = "";

    public string VrstaNaloga { get; set; } = "";
    public string Nalog { get; set; } = "";

    public int? BrojStavki { get; set; }
    public string Kupac { get; set; } = "";
}

public sealed class BoDocumentError
{
    public long? GreskaId { get; set; }

    public string Dokument { get; set; } = "";
    public string IzvorniBroj { get; set; } = "";
    public string Ident { get; set; } = "";

    public string Faza { get; set; } = "";
    public int? BrojGreske { get; set; }
    public string Poruka { get; set; } = "";

    public string Procedura { get; set; } = "";
    public int? Linija { get; set; }
}