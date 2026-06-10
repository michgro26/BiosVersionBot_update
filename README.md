# BiosVersionBot

Bot konsolowy C# do sprawdzania wersji BIOS przez WMI i zapisywania wyniku do relacyjnej bazy kampanii.

## Co robi

- łączy się z MSSQL przez `secureconn.dat`,
- pracuje na kampanii wskazanej przez `Table.CampaignId`,
- pobiera komputery z `dbo.OHD_CAMPAIGN_ITEMS`,
- uwzględnia tylko rekordy:
  - `CAMPAIGN_ID = CampaignId`,
  - `IS_ACTIVE = 1`,
  - `ITEM_STATE = 'Do realizacji'`,
  - `LAST_SCAN IS NULL` albo starszy niż `PERIOD`,
- sprawdza dostępność hostów batchami po 5,
- odczytuje BIOS przez WMI `Win32_BIOS`,
- aktualizuje `OHD_CAMPAIGN_ITEMS`,
- zapisuje historię do `OHD_CAMPAIGN_ITEM_HISTORY`,
- przy wpisie historii pobiera snapshot kontekstu z `v_OHD_CAMPAIGN_ITEMS_FULL`,
- działa w pętli do `END_HOUR`, z przerwą `DELAY` minut.

## Wyniki

Bot zapisuje:

- `LAST_SCAN` — data i godzina próby skanu,
- `OPERATOR = 'Hades2BIOSVersion'`,
- `ITEM_RESULT`:
  - `OFFLINE` — host niedostępny,
  - odczytany BIOS, np. `T74 Ver. 01.23.00`,
  - `BŁĄD` — błąd odczytu BIOS,
- `ITEM_STATE = 'Zrobione'` tylko wtedy, gdy `ITEM_RESULT = 'T74 Ver. 01.23.00'`.

## Audit trail

Po skutecznej aktualizacji pozycji bot dodaje wpis do:

`dbo.OHD_CAMPAIGN_ITEM_HISTORY`

Typ zdarzenia:

- `STATE_CHANGED` — gdy BIOS jest zgodny i rekord przechodzi na `Zrobione`,
- `SCAN_FAILED` — gdy wynik to `OFFLINE` albo `BŁĄD` / BIOS inny niż oczekiwany.

Historia zawiera stare i nowe wartości oraz pola `CTX_*` pobrane z widoku:

`dbo.v_OHD_CAMPAIGN_ITEMS_FULL`

## Konfiguracja

Najważniejsze pola w `appsettings.json`:

```json
{
  "Table": {
    "CampaignId": 12,
    "Name": "OHD_CAMPAIGN_ITEMS",
    "ComputerNameColumn": "COMPUTER_NAME",
    "DescriptionColumn": "ITEM_STATE",
    "LastScanColumn": "LAST_SCAN",
    "OperatorColumn": "OPERATOR",
    "ResultColumn": "ITEM_RESULT",
    "TargetDescriptionValue": "Do realizacji",
    "DoneDescriptionValue": "Zrobione",
    "BotOperatorValue": "Hades2BIOSVersion",
    "ExpectedSuccessResultValue": "T74 Ver. 01.23.00"
  },
  "Runner": {
    "END_HOUR": 18,
    "DELAY": 30,
    "PERIOD": 2,
    "BATCH_SIZE": 5,
    "MAX_PARALLEL": 5
  }
}
```

## Uruchomienie normalne

```powershell
dotnet run
```

albo po buildzie:

```powershell
BiosVersionBot.exe
```

## Tryb verbose / computerlist

Plik `computers.txt`:

```text
A0001-OSWIECIM
A0002-KRAKOW
```

Uruchomienie:

```powershell
dotnet run -- -verbose computers.txt
```

albo:

```powershell
BiosVersionBot.exe -verbose computers.txt
```

W trybie verbose bot bada tylko stacje z pliku i nie filtruje ich po `LAST_SCAN/PERIOD`. Przed zapisem nadal sprawdza, czy rekord należy do kampanii, jest aktywny i ma `ITEM_STATE = 'Do realizacji'`, żeby nie nadpisać zmian ręcznych.
