using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.SqlTypes;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AswTransferToPantheon.Infrastructure.Models;

namespace AswTransferToPantheon.Services.Helpers;

public static class WmsDocumentResultReader
{
    public const string ProcedureName = "dbo._pr_KreirajNalogeIzWMS";

    public static async Task ReadAsync(
        DbDataReader reader, WmsDocumentRunResult result, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(result);
        token.ThrowIfCancellationRequested();
        Check(!result.ResponseComplete && result.Summary is null
            && result.Journals.Count == 0 && result.Errors.Count == 0,
            "WMS rezultati su već učitani. Za novi poziv koristite novi rezultat.");
        Check(result.Vrsta is "KUF" or "VLP", "WMS rezultat nema traženu vrstu KUF ili VLP.");
        Check(result.DatumOd.Date <= result.DatumDo.Date, "WMS rezultat ima neispravan period.");

        var summarySeen = false;
        var journalsSeen = false;
        var errorsSeen = false;
        var previewSeen = false;
        var sqlErrorCount = 0;
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        var createdKeys = new HashSet<string>(StringComparer.Ordinal);
        var previewTotals = new Dictionary<string, PreviewTotals>(StringComparer.Ordinal);

        do
        {
            token.ThrowIfCancellationRequested();
            var row = new ResultColumns(reader);
            if (row.Has("IzabranoNaloga"))
            {
                Check(!summarySeen, "Ponovljen zbirni WMS rezultat.");
                summarySeen = true;
                row.Require("ImportId", "Vrsta", "SamoProvera", "IzabranoNaloga",
                    "ObradjenoNaloga", "KreiranoNaloga", "PredlozenoNaloga",
                    "PreskocenoNaloga", "BrojGresaka", "NalogaSaRazlikom", "UkupnoSek");
                Check(await reader.ReadAsync(token), "Nedostaje zbirni WMS red.");
                var summary = new WmsDocumentSummary
                {
                    ImportId = row.Guid("ImportId"),
                    Vrsta = row.Text("Vrsta"),
                    SamoProvera = row.Boolean("SamoProvera"),
                    IzabranoNaloga = row.RequiredInteger("IzabranoNaloga"),
                    ObradjenoNaloga = row.RequiredInteger("ObradjenoNaloga"),
                    KreiranoNaloga = row.RequiredInteger("KreiranoNaloga"),
                    PredlozenoNaloga = row.RequiredInteger("PredlozenoNaloga"),
                    PreskocenoNaloga = row.RequiredInteger("PreskocenoNaloga"),
                    BrojGresaka = row.RequiredInteger("BrojGresaka"),
                    NalogaSaRazlikom = row.RequiredInteger("NalogaSaRazlikom"),
                    UkupnoSek = row.RequiredDecimal("UkupnoSek")
                };
                Check(summary.ImportId != System.Guid.Empty, "WMS odgovor nema ImportId.");
                Check(summary.Vrsta == result.Vrsta && summary.SamoProvera == result.SamoProvera,
                    "Vrsta ili režim WMS odgovora ne odgovara pozivu procedure.");
                Check(new[] { summary.IzabranoNaloga, summary.ObradjenoNaloga,
                    summary.KreiranoNaloga, summary.PredlozenoNaloga, summary.PreskocenoNaloga,
                    summary.BrojGresaka, summary.NalogaSaRazlikom }.All(x => x >= 0)
                    && summary.UkupnoSek >= 0, "WMS zbirni rezultat sadrži negativne brojače ili trajanje.");
                Check(result.SamoProvera ? summary.KreiranoNaloga == 0 : summary.PredlozenoNaloga == 0,
                    "WMS zbirni rezultat meša probnu i stvarnu obradu.");
                Check(!await reader.ReadAsync(token), "WMS odgovor ima više od jednog zbirnog reda.");
                result.Summary = summary;
            }
            else if (row.Has("NalogID", "Status"))
            {
                Check(!journalsSeen, "Ponovljen spisak WMS naloga.");
                journalsSeen = true;
                row.Require("NalogID", "VrstaNaloga", "Broj", "Datum", "acDocType", "acKey",
                    "acNote", "Status", "BrojStavki", "BrojZamenaKonta", "Duguje", "Potrazuje", "Razlika");
                while (await reader.ReadAsync(token))
                {
                    token.ThrowIfCancellationRequested();
                    var item = new WmsJournalResult
                    {
                        NalogId = row.Id("NalogID", required: true),
                        VrstaNaloga = row.Text("VrstaNaloga"),
                        Broj = row.Text("Broj"),
                        Datum = row.Date("Datum"),
                        AcDocType = row.Text("acDocType"),
                        AcKey = row.Text("acKey"),
                        AcNote = row.Text("acNote"),
                        Status = row.Text("Status"),
                        BrojStavki = row.RequiredInteger("BrojStavki"),
                        BrojZamenaKonta = row.RequiredInteger("BrojZamenaKonta"),
                        Duguje = row.Decimal("Duguje"),
                        Potrazuje = row.Decimal("Potrazuje"),
                        Razlika = row.Decimal("Razlika")
                    };
                    Check(sourceIds.Add(item.NalogId), $"Ponovljen WMS NalogID={item.NalogId} u SQL odgovoru.");
                    ValidateJournal(item, result);
                    if (item.Status == "Kreiran")
                        Check(createdKeys.Add(item.AcKey), $"Ključ {item.AcKey} prijavljen je za više novih WMS naloga.");
                    result.Journals.Add(item);
                    if (item.HasBalanceDifference)
                        result.Errors.Add(new WmsProcessingError
                        {
                            NalogId = item.NalogId,
                            VrstaNaloga = item.VrstaNaloga,
                            Broj = item.Broj,
                            Datum = item.Datum,
                            AcKey = item.AcKey,
                            AcDocType = item.AcDocType,
                            Procedura = ProcedureName,
                            Faza = "Provera ravnoteže naloga",
                            Poruka = FormattableString.Invariant(
                                $"Nalog ima razliku: duguje={item.Duguje:0.####}; potražuje={item.Potrazuje:0.####}; razlika={item.Razlika:0.####}.")
                        });
                }
            }
            else if (row.Has("NalogID", "Poruka"))
            {
                Check(!errorsSeen, "Ponovljen spisak WMS grešaka.");
                errorsSeen = true;
                row.Require("NalogID", "VrstaNaloga", "Broj", "Datum", "Faza",
                    "BrojGreske", "Poruka", "Procedura", "Linija");
                while (await reader.ReadAsync(token))
                {
                    token.ThrowIfCancellationRequested();
                    var error = new WmsProcessingError
                    {
                        NalogId = row.Id("NalogID", required: false),
                        VrstaNaloga = row.Text("VrstaNaloga"),
                        Broj = row.Text("Broj"),
                        Datum = row.Date("Datum"),
                        Faza = row.Text("Faza"),
                        BrojGreske = row.Integer("BrojGreske"),
                        Poruka = row.Text("Poruka"),
                        Procedura = row.Text("Procedura"),
                        Linija = row.Integer("Linija")
                    };
                    Check(error.BrojGreske is > 0 && error.Poruka.Length > 0,
                        "WMS greška nema broj greške ili poruku.");
                    Check(error.NalogId.Length == 0 || sourceIds.Add(error.NalogId),
                        $"NalogID={error.NalogId} ima više ishoda u WMS odgovoru.");
                    // Datum/faza mogu biti NULL kada SQL nije stigao da pročita izvorni nalog.
                    result.Errors.Add(error);
                    sqlErrorCount++;
                }
            }
            else if (row.Has("NalogID", "anNo", "acAcct"))
            {
                Check(result.SamoProvera && !previewSeen,
                    "Neočekivan ili ponovljen detaljan WMS predlog.");
                previewSeen = true;
                row.Require("NalogID", "anNo", "acAcct", "anDebit", "anCredit");
                // Ne čuvamo hiljade stavki u modelu za mail; proveravamo broj i zbirove po nalogu.
                while (await reader.ReadAsync(token))
                {
                    token.ThrowIfCancellationRequested();
                    var id = row.Id("NalogID", required: true);
                    var line = row.RequiredDecimal("anNo");
                    Check(line > 0 && line == decimal.Truncate(line) && line <= long.MaxValue,
                        $"WMS predlog za NalogID={id} nema ispravan broj stavke.");
                    Check(row.Text("acAcct").Length > 0, $"WMS predlog za NalogID={id} nema konto.");
                    if (!previewTotals.TryGetValue(id, out var totals))
                        previewTotals[id] = totals = new PreviewTotals();
                    totals.Count++;
                    // SQL sažetak takođe sabira CAST(anDebit/anCredit AS decimal(28,4)).
                    totals.Debit += decimal.Round(row.RequiredDecimal("anDebit"), 4, MidpointRounding.AwayFromZero);
                    totals.Credit += decimal.Round(row.RequiredDecimal("anCredit"), 4, MidpointRounding.AwayFromZero);
                }
            }
            else
            {
                // Sistemske procedure mogu vratiti dodatni grid. Čitanje do kraja otkriva i kasni SQL izuzetak.
                while (await reader.ReadAsync(token))
                    token.ThrowIfCancellationRequested();
            }
        }
        while (await reader.NextResultAsync(token));

        token.ThrowIfCancellationRequested();
        Check(summarySeen && journalsSeen && errorsSeen && result.Summary is not null,
            "Nepotpun WMS odgovor: nedostaje sažetak, spisak naloga ili spisak grešaka.");
        Check(!result.SamoProvera || previewSeen, "Nepotpun WMS odgovor: nedostaje detaljan probni predlog.");
        var summaryResult = result.Summary!;
        Check(summaryResult.IzabranoNaloga == summaryResult.ObradjenoNaloga
            && (long)summaryResult.ObradjenoNaloga == (long)result.Journals.Count + sqlErrorCount
            && summaryResult.KreiranoNaloga == result.Journals.Count(x => x.Status == "Kreiran")
            && summaryResult.PredlozenoNaloga == result.Journals.Count(x => x.Status == "Predlog")
            && summaryResult.PreskocenoNaloga == result.Journals.Count(x => x.Status == "Preskocen")
            && summaryResult.BrojGresaka == sqlErrorCount
            && summaryResult.NalogaSaRazlikom == result.Journals.Count(x => x.HasBalanceDifference),
            "WMS brojači ne odgovaraju vraćenim nalozima, greškama ili razlikama.");

        if (result.SamoProvera)
        {
            var proposed = result.Journals.Where(x => x.Status == "Predlog").ToArray();
            Check(previewTotals.Count == proposed.Length,
                "Detaljan WMS predlog ne odgovara spisku predloženih naloga.");
            foreach (var journal in proposed)
                Check(previewTotals.TryGetValue(journal.NalogId, out var totals)
                    && totals.Count == journal.BrojStavki
                    && totals.Debit == journal.Duguje && totals.Credit == journal.Potrazuje,
                    $"Broj ili zbirovi probnih stavki ne odgovaraju WMS NalogID={journal.NalogId}.");
        }
        result.ResponseComplete = true;
    }

    private static void ValidateJournal(WmsJournalResult item, WmsDocumentRunResult run)
    {
        Check(item.Status == "Preskocen" || (run.SamoProvera ? item.Status == "Predlog" : item.Status == "Kreiran"),
            $"Neočekivan WMS status '{item.Status}' za NalogID={item.NalogId}.");
        Check(item.Datum.HasValue && item.Datum.Value.Date >= run.DatumOd.Date
            && item.Datum.Value.Date <= run.DatumDo.Date,
            $"WMS NalogID={item.NalogId} nema datum u traženom periodu.");
        Check(item.BrojStavki >= 0 && item.BrojZamenaKonta >= 0 && item.BrojZamenaKonta <= item.BrojStavki,
            $"WMS NalogID={item.NalogId} ima neispravan broj stavki ili zamena konta.");
        if (item.Status is "Kreiran" or "Preskocen")
            Check(item.AcKey.Length is > 0 and <= 13,
                $"WMS NalogID={item.NalogId} nema ispravan ključ naloga.");
        if (item.Status is "Kreiran" or "Predlog")
        {
            Check(item.AcDocType.Length is > 0 and <= 4 && item.BrojStavki > 0,
                $"WMS NalogID={item.NalogId} nema vrstu naloga ili stavke.");
            Check(item.Duguje.HasValue && item.Potrazuje.HasValue && item.Razlika.HasValue,
                $"WMS NalogID={item.NalogId} nema kompletne iznose.");
            Check(item.Duguje.Value - item.Potrazuje.Value == item.Razlika.Value,
                $"WMS NalogID={item.NalogId}: prijavljena razlika ne odgovara iznosima.");
        }
        else
        {
            Check(item.BrojStavki == 0 && item.BrojZamenaKonta == 0,
                $"Preskočen WMS NalogID={item.NalogId} prijavljuje novi upis stavki.");
            Check(item.Duguje is null && item.Potrazuje is null && item.Razlika is null,
                $"Preskočen WMS NalogID={item.NalogId} ima neočekivane iznose.");
        }
    }

    private static void Check([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class PreviewTotals
    {
        public long Count;
        public decimal Debit;
        public decimal Credit;
    }

    private sealed class ResultColumns
    {
        private readonly DbDataReader reader;
        private readonly Dictionary<string, int> columns = new(StringComparer.OrdinalIgnoreCase);
        private readonly bool duplicateNames;

        public ResultColumns(DbDataReader reader)
        {
            this.reader = reader;
            for (var i = 0; i < reader.FieldCount; i++)
                if (!columns.TryAdd(reader.GetName(i), i))
                    duplicateNames = true;
        }

        public bool Has(params string[] names) => names.All(columns.ContainsKey);

        public void Require(params string[] names)
        {
            Check(!duplicateNames, "WMS rezultat sadrži ponovljene nazive kolona.");
            var missing = names.Where(x => !columns.ContainsKey(x)).ToArray();
            Check(missing.Length == 0, "WMS rezultatu nedostaju kolone: " + string.Join(", ", missing));
        }

        private int Ordinal(string name)
        {
            Check(columns.TryGetValue(name, out var ordinal), $"Nedostaje WMS kolona {name}.");
            return ordinal;
        }

        private object? Value(string name)
        {
            var ordinal = Ordinal(name);
            return reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
        }

        public string Text(string name) => Convert.ToString(Value(name), CultureInfo.InvariantCulture)?.Trim() ?? "";

        public string Id(string name, bool required)
        {
            var ordinal = Ordinal(name);
            if (reader.IsDBNull(ordinal))
            {
                Check(!required, $"WMS kolona {name} je NULL.");
                return "";
            }
            // GetValue/GetDecimal mogu prekoračiti .NET decimal za SQL decimal(38,0).
            // Provider-specific vrednost zadržava punu preciznost SQL ID-a.
            var value = reader.GetProviderSpecificValue(ordinal);
            string text;
            if (value is SqlDecimal sqlNumber)
            {
                Check(!sqlNumber.IsNull && (sqlNumber == SqlDecimal.Truncate(sqlNumber, 0)).IsTrue,
                    $"WMS kolona {name} nije ceo broj.");
                text = SqlDecimal.ConvertToPrecScale(sqlNumber, 38, 0).ToString();
            }
            else
            {
                Check(value is string or decimal or byte or short or int or long or ushort or uint or ulong or BigInteger,
                    $"WMS kolona {name} nema tačan numerički tip za ID.");
                if (value is decimal number)
                {
                    Check(number == decimal.Truncate(number), $"WMS kolona {name} nije ceo broj.");
                    text = number.ToString("0", CultureInfo.InvariantCulture);
                }
                else
                    text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            }
            Check(BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id)
                && id > 0 && id < BigInteger.Pow(10, 38), $"WMS kolona {name} nema ispravan pozitivan ID.");
            return id.ToString(CultureInfo.InvariantCulture);
        }

        public decimal? Decimal(string name)
        {
            var value = Value(name);
            if (value is null)
                return null;
            return value is SqlDecimal number ? number.Value : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }

        public decimal RequiredDecimal(string name) =>
            Decimal(name) ?? throw new InvalidOperationException($"WMS kolona {name} je NULL.");

        public int? Integer(string name)
        {
            var number = Decimal(name);
            if (number is null)
                return null;
            Check(number == decimal.Truncate(number.Value) && number >= int.MinValue && number <= int.MaxValue,
                $"WMS kolona {name} nema ispravan ceo broj.");
            return (int)number.Value;
        }

        public int RequiredInteger(string name) => Integer(name)
            ?? throw new InvalidOperationException($"WMS kolona {name} je NULL.");

        public bool Boolean(string name)
        {
            if (Value(name) is bool flag)
                return flag;
            var number = RequiredInteger(name);
            Check(number is 0 or 1, $"WMS kolona {name} nije 0 ili 1.");
            return number == 1;
        }

        public DateTime? Date(string name)
        {
            var value = Value(name);
            Check(value is null or DateTime, $"WMS kolona {name} nema ispravan datum.");
            return (DateTime?)value;
        }

        public Guid Guid(string name) => Value(name) is System.Guid id
            ? id : throw new InvalidOperationException($"WMS kolona {name} nema ispravan GUID.");
    }
}
