using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AswTransferToPantheon.Infrastructure.Models;

namespace AswTransferToPantheon.Services.Helpers;

public static class BoDocumentResultReader
{
    public static async Task ReadAsync(
        DbDataReader reader,
        BoDocumentRunResult result,
        CancellationToken token)
    {
        result.ResponseComplete = false;

        var summaryRead = false;
        var documentsRead = false;
        var errorsRead = false;

        do
        {
            var columns = new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase);

            var duplicateColumns = false;

            for (var index = 0; index < reader.FieldCount; index++)
            {
                if (!columns.TryAdd(reader.GetName(index), index))
                    duplicateColumns = true;
            }

            bool Has(params string[] names) =>
                names.All(columns.ContainsKey);

            var isSummary = !result.SamoProvera
                && Has(
                    "ImportId", "Status", "Datum",
                    "KreiranoDokumenata", "KreiranoStavki",
                    "BrojGresaka");

            var isDocuments =
                (!result.SamoProvera
                    && Has(
                        "ImportId", "Dokument", "RobniDokument",
                        "VrstaDokumenta", "BrojStavki"))
                || (result.SamoProvera
                    && Has(
                        "Dokument", "Datum", "Kupac", "PrviIdent",
                        "VrstaDokumenta", "BrojStavki"));

            var isErrors =
                (!result.SamoProvera
                    && Has(
                        "GreskaId", "ImportId", "Dokument",
                        "Faza", "BrojGreske", "Poruka"))
                || (result.SamoProvera
                    && Has(
                        "Datum", "Dokument", "IzvorniBroj",
                        "DocumentType", "BrojGreske", "Poruka"));

            // Preskačemo dodatne rezultate i opcione podatke o trajanju.
            if (!isSummary && !isDocuments && !isErrors)
                continue;

            if (duplicateColumns)
            {
                throw new InvalidOperationException(
                    "BO rezultat sadrži ponovljene nazive kolona.");
            }

            object? Value(string name) =>
                columns.TryGetValue(name, out var ordinal)
                    && !reader.IsDBNull(ordinal)
                    ? reader.GetValue(ordinal)
                    : null;

            string Text(string name) =>
                Value(name)?.ToString() ?? "";

            int? Integer(string name) =>
                Value(name) is { } value
                    ? Convert.ToInt32(value)
                    : null;

            long? Long(string name) =>
                Value(name) is { } value
                    ? Convert.ToInt64(value)
                    : null;

            decimal? Number(string name) =>
                Value(name) is { } value
                    ? Convert.ToDecimal(value)
                    : null;

            DateTime? Date(string name) =>
                Value(name) is { } value
                    ? Convert.ToDateTime(value)
                    : null;

            void CheckImportId()
            {
                if (result.SamoProvera)
                    return;

                var id = Long("ImportId");

                if (id is null or <= 0)
                {
                    throw new InvalidOperationException(
                        "BO rezultat nema ispravan ImportId.");
                }

                if (result.ImportId.HasValue && result.ImportId != id)
                {
                    throw new InvalidOperationException(
                        "BO odgovor sadrži različite ImportId vrednosti.");
                }

                result.ImportId = id;
            }

            // Rezultate prepoznajemo po kolonama, ne po rednom broju.
            if (isSummary)
            {
                while (await reader.ReadAsync(token))
                {
                    if (summaryRead)
                    {
                        throw new InvalidOperationException(
                            "BO odgovor ima više zbirnih redova.");
                    }

                    summaryRead = true;
                    CheckImportId();

                    if (Date("Datum")?.Date != result.Datum.Date)
                    {
                        throw new InvalidOperationException(
                            "Datum BO odgovora ne odgovara traženom datumu.");
                    }

                    result.Status = Text("Status");

                    result.Summary = new BoDocumentSummary
                    {
                        IzabranoDokumenata = Integer("IzabranoDokumenata"),
                        KreiranoDokumenata = Integer("KreiranoDokumenata"),
                        KreiranoStavki = Integer("KreiranoStavki"),
                        BrojGresaka = Integer("BrojGresaka"),
                        Nalog = Text("Nalog"),
                        PreostaloDokumenataZaDatum =
                            Integer("PreostaloDokumenataZaDatum"),
                        IzabranoZaKnjizenje = Integer("IzabranoZaKnjizenje"),
                        KnjizenoDokumenata = Integer("KnjizenoDokumenata"),
                        GreskeKnjizenja = Integer("GreskeKnjizenja"),
                        DugujeNaloga = Number("DugujeNaloga"),
                        PotrazujeNaloga = Number("PotrazujeNaloga"),
                        RazlikaZaIzravnanje = Number("RazlikaZaIzravnanje")
                    };
                }
            }
            else if (isDocuments)
            {
                if (documentsRead)
                {
                    throw new InvalidOperationException(
                        "BO odgovor ima više spiskova dokumenata.");
                }

                documentsRead = true;

                while (await reader.ReadAsync(token))
                {
                    CheckImportId();

                    result.Documents.Add(new BoImportedDocument
                    {
                        Dokument = Text("Dokument"),
                        Datum = Date("Datum"),
                        VrstaDokumenta = Text("VrstaDokumenta"),
                        RobniDokument = Text("RobniDokument"),
                        VrstaNaloga = Text("VrstaNaloga"),
                        Nalog = Text("Nalog"),
                        BrojStavki = Integer("BrojStavki"),
                        Kupac = Text("Kupac")
                    });
                }
            }
            else if (isErrors)
            {
                if (errorsRead)
                {
                    throw new InvalidOperationException(
                        "BO odgovor ima više spiskova grešaka.");
                }

                errorsRead = true;

                while (await reader.ReadAsync(token))
                {
                    CheckImportId();

                    result.Errors.Add(new BoDocumentError
                    {
                        GreskaId = Long("GreskaId"),
                        Dokument = Text("Dokument"),
                        IzvorniBroj = Text("IzvorniBroj"),
                        Ident = Text("Ident"),
                        Faza = result.SamoProvera
                            ? "Provera dokumenta"
                            : Text("Faza"),
                        BrojGreske = Integer("BrojGreske"),
                        Poruka = Text("Poruka"),
                        Procedura = Text("Procedura"),
                        Linija = Integer("Linija")
                    });
                }
            }
        }
        while (await reader.NextResultAsync(token));

        if (!documentsRead
            || !errorsRead
            || (!result.SamoProvera && !summaryRead))
        {
            throw new InvalidOperationException(
                "Nedostaje kompletan BO rezultat. Proverite verziju " +
                "procedure; uvoz se ne prijavljuje kao uspešan.");
        }

        if (result.SamoProvera)
        {
            result.Status = result.Errors.Count == 0
                ? "Validated"
                : "ValidationWithErrors";

            result.Summary = new BoDocumentSummary
            {
                IzabranoDokumenata = result.Documents
                    .Select(x => x.Dokument)
                    .Concat(result.Errors.Select(x => x.Dokument))
                    .Distinct()
                    .Count(),
                KreiranoDokumenata = 0,
                KreiranoStavki = 0,
                BrojGresaka = result.Errors.Count
            };
        }

        result.ResponseComplete = true;
    }
}