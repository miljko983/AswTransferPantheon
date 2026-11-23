using System;
using System.Collections.Generic;
using System.Linq;

namespace AswTransferToPantheon.Infrastructure.Models;

public sealed class WmsDocumentRunResult
{
    // Rezim poziva: KUF ili VLP.
    public string Vrsta { get; set; } = "";
    public DateTime DatumOd { get; set; }
    public DateTime DatumDo { get; set; }
    public bool SamoProvera { get; set; }

    public WmsDocumentSummary? Summary { get; set; }
    public Guid? ImportId => Summary?.ImportId;

    public List<WmsJournalResult> Journals { get; } = [];
    public List<WmsProcessingError> Errors { get; } = [];

    // Postavlja se tek kada su procitani i provereni svi ocekivani rezultati.
    // Sam primljeni summary ne znaci da je ceo odgovor uspesno procitan.
    public bool ResponseComplete { get; set; }
    public TimeSpan Duration { get; set; }

    public bool HasErrors =>
        !ResponseComplete
        || Summary is null
        || Errors.Count > 0
        || (Summary?.BrojGresaka ?? 0) > 0
        || (Summary?.NalogaSaRazlikom ?? 0) > 0
        || Journals.Any(x => x.HasBalanceDifference);

    // Samo stvarno kreirani nalozi; Predlog i Preskocen ne salju success mail.
    // Potvrdjeni pojedinacni rezultati mogu postojati i uz nepotpun odgovor.
    // Tada HasErrors ostaje true i obavestenje mora jasno oznaciti nepotpunost.
    public bool HasNewWork =>
        !SamoProvera
        && Summary?.SamoProvera != true
        && Journals.Any(x => x.Status == "Kreiran" && !string.IsNullOrWhiteSpace(x.AcKey));
}

public sealed class WmsDocumentSummary
{
    public Guid ImportId { get; set; }
    public string Vrsta { get; set; } = "";
    public bool SamoProvera { get; set; }

    public int IzabranoNaloga { get; set; }
    public int ObradjenoNaloga { get; set; }
    public int KreiranoNaloga { get; set; }
    public int PredlozenoNaloga { get; set; }
    public int PreskocenoNaloga { get; set; }
    public int BrojGresaka { get; set; }
    public int NalogaSaRazlikom { get; set; }
    public decimal UkupnoSek { get; set; }
}

public sealed class WmsJournalResult
{
    // SQL NalogID je decimal(38,0); tekst cuva pun ID bez suzavanja na long/decimal.
    public string NalogId { get; set; } = "";
    public string VrstaNaloga { get; set; } = "";
    public string Broj { get; set; } = "";
    public DateTime? Datum { get; set; }

    public string AcDocType { get; set; } = "";
    public string AcKey { get; set; } = "";
    public string AcNote { get; set; } = "";

    // Kreiran, Predlog ili Preskocen. SQL greske su u zasebnom rezultatu.
    public string Status { get; set; } = "";
    public int BrojStavki { get; set; }
    public int BrojZamenaKonta { get; set; }

    // Kod Preskocen zbirni iznosi mogu biti NULL i ostaju nepoznati, a ne nula.
    public decimal? Duguje { get; set; }
    public decimal? Potrazuje { get; set; }
    public decimal? Razlika { get; set; }

    public bool HasBalanceDifference =>
        (Razlika.HasValue && Razlika.Value != 0m)
        || (Duguje.HasValue && Potrazuje.HasValue && Duguje.Value != Potrazuje.Value);
}

public sealed class WmsProcessingError
{
    // Prazno ako greska nije vezana za konkretan izvorni nalog.
    public string NalogId { get; set; } = "";
    public string VrstaNaloga { get; set; } = "";
    public string Broj { get; set; } = "";
    public DateTime? Datum { get; set; }

    public string Procedura { get; set; } = "";
    public string Faza { get; set; } = "";
    public int? BrojGreske { get; set; }
    public int? Linija { get; set; }
    public string Poruka { get; set; } = "";

    // Dostupno npr. kada aplikacija prijavljuje razliku kreiranog naloga.
    public string AcKey { get; set; } = "";
    public string AcDocType { get; set; } = "";
}
