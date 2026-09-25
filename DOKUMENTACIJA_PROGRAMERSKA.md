# ASW Transfer to Pantheon — programerska dokumentacija

Datum pregleda koda: 24.09.2026.

## 1. Namena aplikacije

`AswTransferToPantheon` je WPF aplikacija za automatizovani prenos podataka iz Oracle ASW baze u SQL Server bazu `TRANSFER`, kao i za izvršavanje poslovnih procedura u bazama `CL_WMS` i `CENTROSINERGIJA`.

Glavni zadaci aplikacije su:

- prenos artikala i šifarnika;
- prenos cenovnika;
- prenos KIF-a i KIF stavki;
- kreiranje identa u bazi `CENTROSINERGIJA` nakon KIF prenosa;
- prenos `VLPIzvSve` podataka;
- kreiranje dokumenata u `CL_WMS` bazi;
- prenos zatvorenih naloga;
- zapisivanje rada u fajlove;
- slanje zbirnih email obaveštenja.

## 2. Struktura rešenja

Rešenje se sastoji od tri projekta.

### `AswTransferToPantheon`

WPF izvršni projekat. Sadrži:

- `App.xaml.cs` — pokretanje Host-a, Dependency Injection-a i glavnog prozora;
- `MainWindow.xaml` — prikaz izvora, cilja i poruka;
- `MainWindowViewModel.cs` — povezivanje scheduler-a sa korisničkim interfejsom.

### `AswTransferToPantheon.Infrastructure`

Sadrži modele, konfiguracione klase i enum vrednosti:

- modeli redova (`Artikal`, `Kif`, `Vlp...`, `Nalog`, `ArtikalMap` itd.);
- `SchedulerConfiguration`, `DailyTask`, `PeriodicTask` i `NaloziTaskConfiguration`;
- `EmailConfiguration` i `TransferEmailConfiguration`;
- `TaskType` enum.

### `AswTransferToPantheon.Services`

Sadrži interfejse i implementacije poslovne logike:

- `TaskSchedulerService`;
- `ArtikliTransferService`;
- `KifTransferService`;
- `VlpIzvSveTransferService`;
- `NaloziTransferService`;
- `DocumentCreationService_CL_WMS`;
- `CentrosinergijaKreiranjeIdenataService`;
- `EmailNotificationService`;
- `TransferFileLogger`.

## 3. Pokretanje aplikacije

`App.xaml.cs` koristi .NET Generic Host.

Prilikom pokretanja:

1. učitava se `appsettings.json` iz foldera izvršnog fajla;
2. registruju se konfiguracije kroz `IOptions<T>`;
3. registruju se transfer servisi kroz Dependency Injection;
4. šalje se test email ako je `Startup.Test` uključen;
5. otvara se glavni prozor;
6. `MainWindowViewModel` pokreće `TaskSchedulerService.ScheduleTasks()`.

`appsettings.json` mora biti prisutan pored `.exe` fajla. To je obezbeđeno kroz `CopyToOutputDirectory` u WPF projektu.

## 4. Scheduler

Glavni scheduler je `TaskSchedulerService`.

Postoje tri vrste rasporeda:

### Dnevni taskovi

Čitaju se iz `Scheduler:DailyTasks`.

`BatchSize` se prosleđuje svim taskovima koji pripadaju dnevnom tasku. Na primer, kod `Artikli` isti `BatchSize` koriste artikli, cenovnik, dobavljači, osobine, barkodovi, robne grupe i `ARTIKLIMAP`.

`ExecuteOnStartup` određuje da li se task izvršava odmah nakon pokretanja aplikacije.

`ExecuteDocumentCreation` određuje da li se nakon uspešnog transfera Artikala pokreće kreiranje `CL_WMS` dokumenata.

### Periodični taskovi

Čitaju se iz `Scheduler:PeriodicTasks`.

Podržani su:

- `Kif`;
- `VLPIzvSve`.

Periodični task se izvršava u vremenskom prozoru `Start`–`End`, u razmaku `PeriodInMinutes`.

### Taskovi za naloge

Čitaju se iz `Scheduler:NaloziTasks`.

Za njih se posebno podešavaju:

- `BatchSize`;
- `DateFrom`;
- `Start`;
- `End`;
- `PeriodInMinutes`;
- `ExecuteOnStartup`.

Task `Nalozi` prenosi samo zapise koji ispunjavaju uslov implementiran u Oracle upitu, uključujući zatvorene naloge.

### Zaustavljanje

Zatvaranjem prozora poziva se `CancelTasks()`, koji otkazuje zajednički `CancellationTokenSource`.

## 5. Tok prenosa Artikala

Ulazna tačka je:

```csharp
IArtikliTransferService.TransferArtikliPaket(...)
```

Trenutni redosled u `ArtikliTransferService` je:

1. `Artikli`;
2. `Cenovnik`;
3. `Artikli dobavljači`;
4. `Artikli osobine`;
5. `Artikli dobavljači`;
6. `Artikli map`;
7. `Barkodovi`;
8. `Robne grupe`;
9. `_pr_CL_WMS_ArtikliUvoz`.

Za svaki tip podataka koristi se paketno čitanje iz Oracle-a, privremena SQL Server tabela i transakcioni upis.

### Napomena za proveru koda

U trenutnom kodu postoji dvostruki poziv:

```csharp
await ExecuteWithLogging("Artikli dobavljači", () => TransferArtikliDobavljaci(batchSize, token));
```

Ako drugi poziv nije nameran, treba ga ukloniti. U suprotnom se dobavljači čitaju i obrađuju dva puta.

## 6. Prenos `ARTIKLIMAP`

`IIS.ARTIKLIMAP` se čita iz Oracle baze u paketima po `ID` vrednosti.

Tok je:

```text
IIS.ARTIKLIMAP
    ↓
ReadArtikliMapBatch
    ↓
ARTIKLIMAP_TMP
    ↓
_pr_MergeArtikliMap
    ↓
TRANSFER.dbo.ARTIKLIMAP
```

Upis je append-only:

- postojeći `ID` se ne menja;
- novi `ID` se dodaje;
- primarni ključ sprečava duplikat;
- `adTimeIns` se automatski popunjava u SQL Serveru;
- model koristi `KolicinaMpjm`, koji odgovara Oracle koloni `KOLICINAMPJM`.

Za ovaj tok aplikacija koristi:

- `ArtikalMap` model;
- `ReadArtikliMapBatch`;
- `TransferArtikliMap`;
- `SaveArtikliMapToTmpTable`;
- `dbo.ARTIKLIMAP_TMP`;
- `dbo._pr_MergeArtikliMap`.

## 7. KIF i kreiranje identa

`TransferKif` prvo pokreće:

```csharp
kifTransferService.Transfer(...)
```

Nakon toga se pokreće:

```csharp
centrosinergijaKreiranjeIdenataService.Execute(...)
```

Procedura `dbo._pr_CENTROSINERGIJA_KreiranjeIdenata` vraća tri result seta:

1. zbirni rezultat;
2. greške i upozorenja;
3. uspešno kreirane idente.

Rezultati se zapisuju u log, a greške i uspešno kreirani identi šalju se zbirno emailom.

## 8. `VLPIzvSve` i kreiranje CL_WMS dokumenata

`TransferVlpIzvSve` prenosi:

- zaglavlja;
- stavke;
- varijante.

Kreiranje dokumenata se poziva iz toka Artikala, nakon završetka Artikala i cenovnika, samo kada su ispunjeni uslovi:

- `ExecuteDocumentCreation = true`;
- nema neispravnih redova u transferu Artikala.

Servis `DocumentCreationService_CL_WMS` poziva:

```text
dbo._pr_KreiranjeDokumenata_CL_WMS
```

Greške se čitaju iz:

```text
dbo._tb_GreskeKreiranjaDokumenata_CLWMS
```

Uspešno kreirana dokumenta i greške šalju se odvojeno kroz zbirna email obaveštenja.

## 9. SQL Server procedure i privremene tabele

### Artikli

- `dbo.ARTIKLI_TMP`;
- `dbo._pr_MergeArtikli`;
- `dbo._pr_CL_WMS_ArtikliUvoz`.

### Cenovnik

- `dbo._tb_CENOVNIK_TMP`;
- `dbo._pr_InsertCenovnik`;
- `dbo.CENOVNIK`.

### Artikli povezani podaci

- `dbo._tb_ARTIKLIDOBAVLJACI_TMP`;
- `dbo._tb_ARTIKLIOSOBINE_TMP`;
- `dbo._tb_BARKODOVI_TMP`;
- `dbo._tb_ROBNEGRUPE_TMP`;
- odgovarajuće `_pr_Merge...` procedure.

### KIF

- `dbo._tb_KIF_TMP`;
- `dbo._tb_KIFSTAVKE_TMP`;
- `dbo._pr_MergeKif`;
- `dbo._pr_MergeKifStavke`.

### VLP

- `dbo._tb_VLPZAGLAVLJA_IZV_SVE_TMP`;
- `dbo._tb_VLPSTAVKE_IZV_SVE_TMP`;
- `dbo._tb_VLPVARIJANTE_IZV_SVE_TMP`;
- odgovarajuće `_pr_Insert...` procedure.

### Nalozi

- `dbo._tb_NALOZI_TMP`;
- `dbo._tb_NALOZISTAVKE_TMP`;
- `dbo._pr_InsertNalozi`.

## 10. Logovanje i greške

`TransferFileLogger` zapisuje:

- informativne poruke;
- greške taska;
- neispravne redove.

Putanja se podešava kroz `Logging:RootPath`.

Greške su podeljene na:

- grešku pojedinačnog reda;
- grešku taska;
- kritičnu grešku.

Kritične greške prekidaju trenutno izvršavanje i dozvoljavaju da sledeći zakazani termin pokuša ponovo.

## 11. Email obaveštenja

Email servis koristi SMTP podešavanja iz `Email` sekcije.

Podržane notifikacije uključuju:

- `Startup.Test`;
- `Artikli.Created`;
- `Artikli.BadRecords`;
- `Kif.BadRecords`;
- `KreiranjeIdenata.BadRecords`;
- `KreiranjeIdenata.Created`;
- `VLPIzvSve.BadRecords`;
- `Nalozi.BadRecords`;
- `Dokumenti.Created`;
- `Dokumenti.CreationErrors`;
- `Task.Error`;
- `Task.CriticalError`.

Primaoci u `To` i `Cc` su liste pojedinačnih email adresa. Više adresa se ne razdvaja tačkom-zarezom u jednoj string vrednosti, već se unose kao zasebni elementi niza.

## 12. Dependency Injection

Servisi se registruju u `App.xaml.cs`.

Transfer servisi su uglavnom `Transient`, dok su logger i email servis `Singleton`. `TaskSchedulerService` je takođe `Singleton`, jer održava scheduler i zajednički cancellation token.

## 13. Bezbednost

Trenutni `appsettings.json` sadrži lozinke za baze i SMTP nalog. Takve vrednosti ne treba čuvati u javnom repozitorijumu niti slati drugim korisnicima.

Pre produkcionog korišćenja preporučuje se:

- promena svih trenutno korišćenih lozinki;
- korišćenje Windows Credential Manager-a, environment varijabli ili Secret Manager-a;
- poseban `appsettings.Production.json` bez čuvanja tajni u izvornom kodu.

## 14. Poznate operativne napomene

- `appsettings.json` mora biti pored `.exe` fajla.
- `executiontimes.json` se čuva uz radni folder aplikacije.
- Privremene tabele se koriste paketno i ne treba ih ručno menjati dok aplikacija radi.
- Ista aplikacija ne treba da se pokreće u dve instance nad istim bazama.
- Za svaki novi SQL objekat potrebno je prvo kreirati tabelu/proceduru, pa tek onda uključiti odgovarajući poziv u aplikaciji.

