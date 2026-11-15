using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AswTransferToPantheon.Infrastructure.Models;

namespace AswTransferToPantheon.Services.Helpers;

public static class KifDocumentResultReader
{
    public const string DocumentsProcedure = "dbo._pr_KreirajDokumenteIzKIF";
    public const string PostingProcedure = "dbo._pr_KreirajNalogeIzKIFMove";

    public static async Task ReadDocumentsAsync(
        DbDataReader reader, KifDocumentRunResult result, CancellationToken token)
    {
        result.DocumentsResponseComplete = false;
        Check(result.Summary is null && result.Documents.Count == 0,
            "Rezultati kreiranja KIF dokumenata već su učitani.");

        var summarySeen = false;
        var documentsSeen = false;
        var ids = new HashSet<int>();

        do
        {
            token.ThrowIfCancellationRequested();
            var row = new ResultColumns(reader);

            if (row.Has("IzabranoKIF"))
            {
                Check(!summarySeen, "Ponovljen zbirni rezultat kreiranja KIF dokumenata.");
                summarySeen = true;
                row.Require("SamoProvera", "IzabranoKIF", "Predlozeno", "Kreirano", "Gresaka");
                Check(await reader.ReadAsync(token), "Nedostaje zbirni red kreiranja KIF dokumenata.");

                var summary = new KifDocumentSummary
                {
                    SamoProvera = row.Boolean("SamoProvera"),
                    IzabranoKIF = row.RequiredInteger("IzabranoKIF"),
                    Predlozeno = row.RequiredInteger("Predlozeno"),
                    Kreirano = row.RequiredInteger("Kreirano"),
                    Gresaka = row.RequiredInteger("Gresaka")
                };
                Check(summary.SamoProvera == result.SamoProvera,
                    "Režim KIF odgovora ne odgovara traženom režimu SamoProvera.");
                Check(summary.IzabranoKIF >= 0 && summary.Predlozeno >= 0
                    && summary.Kreirano >= 0 && summary.Gresaka >= 0,
                    "Zbirni KIF rezultat sadrži negativne brojače.");
                Check(!await reader.ReadAsync(token),
                    "KIF odgovor ima više od jednog zbirnog reda.");
                result.Summary = summary;
            }
            else if (row.Has("KIFID", "Status"))
            {
                Check(!documentsSeen, "Ponovljen spisak rezultata kreiranja KIF dokumenata.");
                documentsSeen = true;
                row.Require("KIFID", "acKey", "acDocType", "Status",
                    "BrojStavki", "Faza", "BrojGreske", "Poruka");

                while (await reader.ReadAsync(token))
                {
                    var item = new KifDocumentResult
                    {
                        KifId = row.RequiredInteger("KIFID"),
                        AcKey = row.Text("acKey"),
                        AcDocType = row.Text("acDocType"),
                        Status = row.Text("Status"),
                        BrojStavki = row.RequiredInteger("BrojStavki"),
                        Faza = row.Text("Faza"),
                        BrojGreske = row.Integer("BrojGreske"),
                        Poruka = row.Text("Poruka")
                    };
                    Check(item.KifId > 0 && ids.Add(item.KifId),
                        $"Neispravan ili ponovljen KIF ID={item.KifId} u SQL odgovoru.");
                    Check(item.BrojStavki >= 0, $"Negativan broj stavki za KIF ID={item.KifId}.");
                    ValidateStatus(item.Status, result.SamoProvera, "Kreiran",
                        item.BrojGreske, item.Poruka);
                    if (item.Status is "Kreiran" or "Predlog")
                        Check(item.AcDocType.Length > 0 && item.BrojStavki > 0,
                            $"KIF ID={item.KifId}: nedostaje vrsta dokumenta ili broj stavki.");
                    if (item.Status is "Kreiran" or "Preskocen")
                        Check(item.AcKey.Length > 0,
                            $"KIF ID={item.KifId}: nedostaje ključ robnog dokumenta.");

                    result.Documents.Add(item);
                    if (item.Status == "Greska")
                        result.Errors.Add(new KifProcessingError
                        {
                            Procedura = DocumentsProcedure,
                            Faza = item.Faza,
                            KifId = item.KifId,
                            AcKey = item.AcKey,
                            AcDocType = item.AcDocType,
                            BrojGreske = item.BrojGreske,
                            Poruka = item.Poruka
                        });
                }
            }
            else
            {
                // Dodatne rezultate čitamo do kraja, da se otkrije i kasnija SQL greška.
                while (await reader.ReadAsync(token)) { }
            }
        }
        while (await reader.NextResultAsync(token));

        token.ThrowIfCancellationRequested();
        Check(summarySeen && documentsSeen && result.Summary is not null,
            "Nepotpun KIF odgovor: nedostaje zbirni rezultat ili spisak dokumenata.");
        var totals = result.Summary!;
        Check(totals.IzabranoKIF == result.Documents.Count
            && totals.Kreirano == result.Documents.Count(x => x.Status == "Kreiran")
            && totals.Predlozeno == result.Documents.Count(x => x.Status == "Predlog")
            && totals.Gresaka == result.Documents.Count(x => x.Status == "Greska"),
            "Zbirni KIF brojači ne odgovaraju vraćenom spisku dokumenata.");
        result.DocumentsResponseComplete = true;
    }

    public static async Task ReadPostingsAsync(
        DbDataReader reader, KifDocumentRunResult result, CancellationToken token)
    {
        result.PostingResponseComplete = false;
        Check(result.Postings.Count == 0, "Rezultati KIF knjiženja već su učitani.");

        var postingsSeen = false;
        var headersSeen = false;
        var moves = new HashSet<string>(StringComparer.Ordinal);
        var headers = new Dictionary<string, (string Type, DateTime Date)>(StringComparer.Ordinal);

        do
        {
            token.ThrowIfCancellationRequested();
            var row = new ResultColumns(reader);

            if (row.Has("MoveKey", "Status"))
            {
                Check(!postingsSeen, "Ponovljen spisak rezultata KIF knjiženja.");
                postingsSeen = true;
                row.Require("MoveKey", "Datum", "VrstaRoba", "VrstaNaloga",
                    "Nalog", "Status", "BrojGreske", "Poruka");

                while (await reader.ReadAsync(token))
                {
                    var item = new KifPostingResult
                    {
                        MoveKey = row.Text("MoveKey"),
                        Datum = row.Date("Datum"),
                        VrstaRoba = row.Text("VrstaRoba"),
                        VrstaNaloga = row.Text("VrstaNaloga"),
                        Nalog = row.Text("Nalog"),
                        Status = row.Text("Status"),
                        BrojGreske = row.Integer("BrojGreske"),
                        Poruka = row.Text("Poruka")
                    };
                    Check(item.MoveKey.Length > 0 && moves.Add(item.MoveKey),
                        $"Neispravan ili ponovljen MoveKey={item.MoveKey} u KIF odgovoru.");
                    Check(item.Datum.Date >= new DateTime(2026, 8, 1)
                        && item.Datum.Date <= result.Datum.Date,
                        $"Dokument {item.MoveKey}: datum knjiženja nije u traženom periodu.");
                    ValidateStatus(item.Status, result.SamoProvera, "Knjizen",
                        item.BrojGreske, item.Poruka);
                    if (item.Status is "Knjizen" or "Predlog")
                        Check(item.VrstaRoba.Length > 0 && item.VrstaNaloga.Length > 0,
                            $"Dokument {item.MoveKey}: nedostaje vrsta dokumenta ili naloga.");
                    if (item.Status == "Knjizen")
                        Check(item.Nalog.Length > 0,
                            $"Dokument {item.MoveKey}: prijavljeno knjiženje bez broja naloga.");

                    result.Postings.Add(item);
                    if (item.Status == "Greska")
                        result.Errors.Add(new KifProcessingError
                        {
                            Procedura = PostingProcedure,
                            Faza = "Knjiženje KIF",
                            AcKey = item.MoveKey,
                            AcDocType = item.VrstaRoba,
                            BrojGreske = item.BrojGreske,
                            Poruka = item.Poruka
                        });
                }
            }
            else if (!result.SamoProvera && row.Has("acKey", "acDocType", "adDate"))
            {
                Check(!headersSeen, "Ponovljen spisak KIF naloga.");
                headersSeen = true;
                row.Require("acKey", "acDocType", "adDate", "anDebit", "anCredit", "Razlika");
                while (await reader.ReadAsync(token))
                {
                    var key = row.Text("acKey");
                    var type = row.Text("acDocType");
                    var date = row.Date("adDate");
                    Check(key.Length > 0 && type.Length > 0
                            && date.Date >= new DateTime(2026, 8, 1)
                            && date.Date <= result.Datum.Date,
                            "KIF odgovor sadrži nalog bez ključa/vrste ili van traženog perioda.");
                    Check(headers.TryAdd(key, (type, date)),
                        $"Nalog {key} je ponovljen u KIF odgovoru.");
                }
            }
            else
            {
                // U proveri se vraćaju i predlozi pozicija; nisu stvarna knjiženja.
                while (await reader.ReadAsync(token)) { }
            }
        }
        while (await reader.NextResultAsync(token));

        token.ThrowIfCancellationRequested();
        Check(postingsSeen, "Nepotpun KIF odgovor: nedostaje spisak rezultata knjiženja.");
        if (!result.SamoProvera)
        {
            Check(headersSeen, "Nepotpun KIF odgovor: nedostaje spisak naloga.");
            var posted = result.Postings.Where(x => x.Status == "Knjizen").ToList();
            Check(headers.Count == posted.Select(x => x.Nalog).Distinct(StringComparer.Ordinal).Count(),
                "Spisak KIF naloga ne odgovara prijavljenim knjiženjima.");
            foreach (var item in posted)
                Check(headers.TryGetValue(item.Nalog, out var header)
                    && header.Type == item.VrstaNaloga && header.Date.Date == item.Datum.Date,
                    $"Nalog {item.Nalog} nije potvrđen odgovarajućim zaglavljem u SQL odgovoru.");
        }
        result.PostingResponseComplete = true;
    }

    private static void ValidateStatus(
        string status, bool preview, string success, int? errorNumber, string message)
    {
        var allowed = status is "Greska" or "Preskocen"
            || (preview ? status == "Predlog" : status == success);
        Check(allowed, $"Nepoznat ili neočekivan KIF status: '{status}'.");
        if (status == "Greska")
            Check(errorNumber is > 0 && !string.IsNullOrWhiteSpace(message),
                "KIF greška nema broj greške ili opis.");
        else
            Check(errorNumber is null, "KIF rezultat prijavljuje grešku uz status koji nije Greska.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
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
            Check(!duplicateNames, "KIF rezultat sadrži ponovljene nazive kolona.");
            var missing = names.Where(x => !columns.ContainsKey(x)).ToArray();
            Check(missing.Length == 0,
                "KIF rezultatu nedostaju kolone: " + string.Join(", ", missing));
        }

        private object? Value(string name)
        {
            Check(columns.TryGetValue(name, out var ordinal), $"Nedostaje KIF kolona {name}.");
            return reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
        }

        public string Text(string name) =>
            Convert.ToString(Value(name), CultureInfo.InvariantCulture)?.Trim() ?? "";

        public int? Integer(string name)
        {
            var value = Value(name);
            if (value is null)
                return null;
            var number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            Check(number == decimal.Truncate(number)
                && number >= int.MinValue && number <= int.MaxValue,
                $"KIF kolona {name} nema ispravan ceo broj.");
            return (int)number;
        }

        public int RequiredInteger(string name) =>
            Integer(name) ?? throw new InvalidOperationException($"KIF kolona {name} je NULL.");

        public bool Boolean(string name)
        {
            var value = Value(name);
            if (value is bool flag)
                return flag;
            var number = RequiredInteger(name);
            Check(number is 0 or 1, $"KIF kolona {name} nije 0 ili 1.");
            return number == 1;
        }

        public DateTime Date(string name) =>
            Value(name) is DateTime date
                ? date
                : throw new InvalidOperationException($"KIF kolona {name} nema ispravan datum.");
    }
}

