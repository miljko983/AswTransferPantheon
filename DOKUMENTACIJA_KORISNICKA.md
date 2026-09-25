# ASW Transfer to Pantheon — korisnička dokumentacija

## 1. Šta aplikacija radi

Aplikacija automatski prenosi podatke iz ASW Oracle baze u SQL Server i izvršava prateće procedure.

Najvažniji prenosi su:

- Artikli i šifarnici;
- Cenovnik;
- KIF;
- kreiranje identa u `CENTROSINERGIJA` bazi;
- `VLPIzvSve`;
- CL_WMS dokumenta;
- zatvoreni nalozi.

## 2. Pokretanje

1. Kopirati kompletan izlazni folder aplikacije, ne samo `.exe` fajl.
2. Proveriti da se u istom folderu nalazi `appsettings.json`.
3. Pokrenuti `AswTransferToPantheon.exe`.
4. Sačekati da se otvori glavni prozor.
5. Proveriti da su prikazane Source i Target baze.
6. Proveriti da nema crvenih poruka.

Prilikom pokretanja aplikacija može poslati test email ako je `Startup.Test` uključen.

## 3. Podešavanje konekcija

U `appsettings.json` podešavaju se:

- Oracle korisnik i lozinka;
- Oracle data source;
- SQL Server connection string za bazu `TRANSFER`.

Primer strukture bez stvarnih lozinki:

```json
{
  "ConnectionStrings": {
    "AswUser": "oracle-korisnik",
    "AswPassword": "oracle-lozinka",
    "AswDataSource": "oracle-data-source",
    "Transfer": "Data Source=SERVER;Initial Catalog=TRANSFER;Integrated Security=True;TrustServerCertificate=True;"
  }
}
```

Posle izmene konekcije potrebno je ponovo pokrenuti aplikaciju.

## 4. Podešavanje rasporeda

### Artikli

Primer:

```json
"DailyTasks": [
  {
    "BatchSize": 1000,
    "Name": "Sifarnici",
    "Start": "23:30:00",
    "ExecuteOnStartup": true,
    "ExecuteDocumentCreation": false,
    "ParallelTasks": [
      "Artikli"
    ]
  }
]
```

`BatchSize` je broj redova po paketu za sve podprocese Artikala, uključujući `ARTIKLIMAP`.

Za testiranje se može staviti:

```json
"BatchSize": 10
```

Posle testa vratiti veću vrednost, na primer `1000`.

### KIF

```json
{
  "Name": "KIF",
  "BatchSize": 100,
  "Start": "08:00:00",
  "End": "23:30:00",
  "PeriodInMinutes": 30,
  "ExecuteOnStartup": true,
  "Tasks": [
    "Kif"
  ]
}
```

Nakon KIF prenosa automatski se pokreće kreiranje identa u `CENTROSINERGIJA` bazi.

### VLPIzvSve

```json
{
  "Name": "VLPIzvSve",
  "BatchSize": 1000,
  "DaysBack": 0,
  "Start": "08:00:00",
  "End": "23:30:00",
  "PeriodInMinutes": 300,
  "ExecuteOnStartup": true,
  "Tasks": [
    "VLPIzvSve"
  ]
}
```

### Nalozi

```json
"NaloziTasks": [
  {
    "Name": "Nalozi",
    "BatchSize": 100,
    "DateFrom": "2026-08-01",
    "Start": "08:00:00",
    "End": "23:30:00",
    "PeriodInMinutes": 10,
    "ExecuteOnStartup": true
  }
]
```

Ovaj task ima sopstveni raspored i ne zavisi od rasporeda Artikala ili KIF-a.

## 5. Kreiranje CL_WMS dokumenata

Kreiranje CL_WMS dokumenata uključuje se kroz:

```json
"ExecuteDocumentCreation": true
```

Kada je `false`, Artikli i ostali šifarnici se prenose, ali se CL_WMS dokumenta ne kreiraju.

Dokumenta se kreiraju nakon uspešnog završetka prenosa Artikala i cenovnika. Ako postoje neispravni redovi u tom transferu, kreiranje dokumenata se preskače.

## 6. Email primaoci

Više primalaca se unosi kao zaseban element niza:

```json
"To": [
  "prvi@example.com",
  "drugi@example.com"
],
"Cc": [
  "kontrola@example.com",
  "administrator@example.com"
]
```

Ne upisivati više adresa kao jednu vrednost sa `;` ili `,`.

Za isključivanje svih emailova:

```json
"Email": {
  "Enabled": false
}
```

Za isključivanje samo jedne vrste poruke, na primer grešaka dokumenata:

```json
"Dokumenti.CreationErrors": {
  "Enabled": false
}
```

## 7. Gde se vide poruke

U glavnom prozoru:

- crvena poruka znači grešku;
- crna poruka znači informaciju ili uspešan završetak.

Detaljni fajlovi se nalaze u folderu podešenom kroz:

```json
"Logging": {
  "RootPath": "D:\\TransferLog",
  "EnableFileLog": true,
  "EnableBadRecordLog": true
}
```

Logovi su organizovani po grupi i tasku, na primer:

```text
DailyTasks\Sifarnici
PeriodicTasks\KIF
PeriodicTasks\VLPIzvSve
NaloziTasks\Nalozi
```

## 8. Provera prenosa `ARTIKLIMAP`

Posle prenosa proveriti broj redova:

```sql
USE [TRANSFER];

SELECT COUNT(*) AS Ukupno
FROM dbo.ARTIKLIMAP;

SELECT TOP (20)
    ID,
    ARTIKAL,
    VARIJANTA,
    KOLICINAMPJM,
    adTimeIns
FROM dbo.ARTIKLIMAP
ORDER BY ID DESC;
```

Ponovljen prenos ne treba da pravi duplikate. Primarni ključ `ID` i procedura za upis samo novih redova sprečavaju ponovno unošenje postojećih zapisa.

## 9. Najčešći problemi

### Aplikacija se ne pokreće dvoklikom

Proveriti:

- da je kopiran ceo folder, a ne samo `.exe`;
- da postoji `appsettings.json` pored `.exe`;
- da su prisutni svi DLL fajlovi;
- da je instaliran odgovarajući .NET 8 Windows runtime.

### SMTP greška `5.7.0 Authentication Required`

Za Gmail je potrebno:

- uključiti 2-Step Verification;
- napraviti App Password;
- App Password upisati u `Email:Password`;
- koristiti SMTP host `smtp.gmail.com`, port `587` i SSL.

### Oracle greška `ORA-00904 invalid identifier`

To znači da naziv kolone u Oracle upitu nije tačan. Proveriti naziv kroz `ALL_TAB_COLUMNS`. Na primer, za `ARTIKLIMAP`:

```sql
SELECT COLUMN_ID, COLUMN_NAME, DATA_TYPE
FROM ALL_TAB_COLUMNS
WHERE OWNER = 'IIS'
  AND TABLE_NAME = 'ARTIKLIMAP'
ORDER BY COLUMN_ID;
```

### SQL timeout

Proveriti:

- da li druga sesija drži zaključavanje;
- da li SQL procedura radi nad velikim brojem redova;
- da li postoje odgovarajući indeksi;
- da li se taskovi preklapaju.

### Strani ključ u CL_WMS ili CENTROSINERGIJA bazi

Proveriti da li postoje:

- odgovarajuća vrsta artikla;
- dobavljač ili komitent;
- odeljenje i nosilac troška;
- korisnik koji kreira dokument;
- mapiranje u odgovarajućoj map tabeli.

## 10. Zaustavljanje i ponovno pokretanje

Zaustaviti aplikaciju zatvaranjem glavnog prozora. Taskovi dobijaju signal za otkazivanje.

Kod ponovnog pokretanja aplikacija nastavlja po pravilima procedura i primarnih ključeva. Već preneti podaci ne bi trebalo da se ponovo upisuju.

## 11. Važna bezbednosna napomena

Ne deliti `appsettings.json` sa stvarnim lozinkama. Ako je fajl već deljen ili kopiran na više računara, preporučuje se promena Oracle, SQL Server i SMTP lozinki.

