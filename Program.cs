using BiosVersionBot.Core;
using BiosVersionBot.Data;
using BiosVersionBot.Networking;
using BiosVersionBot.Security;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BiosVersionBot
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            FileLogger? log = null;

            try
            {
                var cfg = LoadConfig();
                Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "LOG"));
                log = new FileLogger(Path.Combine(AppContext.BaseDirectory, "LOG"));

                bool isVerbose = TryGetComputerList(args, out string? computerListArg);
                string? computerListPath = isVerbose ? ResolveFilePath(computerListArg!) : null;

                log.Info($"START BOT BIOS scanner, verbose={isVerbose}");
                Console.WriteLine(isVerbose ? "== BiosVersionBot - tryb verbose ==" : "== BiosVersionBot - tryb nienadzorowany ==");

                if (isVerbose)
                {
                    try
                    {
                        await RunSingleCycleAsync(cfg, args, log, cts.Token, true, computerListPath);
                        log.Info("END BOT verbose");
                        return 0;
                    }
                    catch (OperationCanceledException)
                    {
                        log.Info("BOT cancelled by user verbose");
                        return 130;
                    }
                    catch (Exception ex)
                    {
                        log.Critical($"CRITICAL verbose error: {ex}");
                        Console.Error.WriteLine($"[CRITICAL] {ex.Message}");
                        return 3;
                    }
                }

                while (!cts.IsCancellationRequested)
                {
                    var now = DateTime.Now;
                    var endTimeToday = now.Date.AddHours(cfg.Runner.END_HOUR);

                    if (now >= endTimeToday)
                    {
                        log.Info($"END_HOUR reached ({cfg.Runner.END_HOUR}:00). Bot stops.");
                        Console.WriteLine($"[INFO] Osiągnięto END_HOUR={cfg.Runner.END_HOUR}:00. Koniec pracy.");
                        break;
                    }

                    try
                    {
                        await RunSingleCycleAsync(cfg, args, log, cts.Token, false, null);
                    }
                    catch (OperationCanceledException)
                    {
                        log.Info("Cycle cancelled by user.");
                        break;
                    }
                    catch (Exception ex)
                    {
                        log.Critical($"CRITICAL cycle error: {ex}");
                        Console.Error.WriteLine($"[CRITICAL] {ex.Message}");
                    }

                    now = DateTime.Now;
                    endTimeToday = now.Date.AddHours(cfg.Runner.END_HOUR);

                    if (now >= endTimeToday)
                    {
                        log.Info($"END_HOUR reached after cycle ({cfg.Runner.END_HOUR}:00). Bot stops.");
                        Console.WriteLine($"[INFO] Osiągnięto END_HOUR={cfg.Runner.END_HOUR}:00 po cyklu. Koniec pracy.");
                        break;
                    }

                    log.Info($"Sleep for {cfg.Runner.DELAY} minute(s) before next cycle.");
                    Console.WriteLine($"[INFO] Przerwa {cfg.Runner.DELAY} min...");
                    await Task.Delay(TimeSpan.FromMinutes(cfg.Runner.DELAY), cts.Token);
                }

                log.Info("END BOT");
                return 0;
            }
            catch (OperationCanceledException)
            {
                log?.Info("BOT cancelled by user.");
                return 130;
            }
            catch (Exception ex)
            {
                log?.Critical($"CRITICAL startup error: {ex}");
                Console.Error.WriteLine($"Błąd krytyczny startu: {ex.Message}");
                return 3;
            }
        }

        private static async Task RunSingleCycleAsync(AppConfig cfg, string[] args, FileLogger log, CancellationToken ct, bool isVerbose, string? computerListPath)
        {
            Console.WriteLine();
            Console.WriteLine("==================================================");
            Console.WriteLine($"[INFO] Start cyklu: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine("==================================================");
            log.Info($"=== START CYCLE === verbose={isVerbose}");

            string secureFile = ResolveSecureFile(cfg, args);
            if (!File.Exists(secureFile))
            {
                string msg = $"Brak pliku z connection stringiem: {secureFile}";
                Console.Error.WriteLine(msg);
                log.Error(msg);
                return;
            }

            IConnectionStringProvider provider = new AesConnectionStringProvider(secureFile);
            string connString = provider.GetConnectionString();

            var csb = new SqlConnectionStringBuilder(connString);
            if (!string.IsNullOrWhiteSpace(cfg.Database.OverrideServer))
                csb.DataSource = cfg.Database.OverrideServer;
            if (cfg.Database.ForceEncrypt.HasValue)
                csb.Encrypt = cfg.Database.ForceEncrypt.Value;
            if (cfg.Database.ForceTrustServerCertificate.HasValue)
                csb.TrustServerCertificate = cfg.Database.ForceTrustServerCertificate.Value;
            connString = csb.ConnectionString;

            Console.WriteLine($"[DEBUG] DataSource: {csb.DataSource}; Catalog: {csb.InitialCatalog}; User: {csb.UserID}");
            Console.WriteLine($"[DEBUG] working on table: {cfg.Table.Schema}.{cfg.Table.Name}; CampaignId={cfg.Table.CampaignId}");
            log.Info($"Conn: {csb.DataSource}; DB: {csb.InitialCatalog}; Table: {cfg.Table.Schema}.{cfg.Table.Name}; CampaignId={cfg.Table.CampaignId}");

            var repo = new BiosScanRepository(
                connString,
                cfg.Table.Schema,
                cfg.Table.Name,
                cfg.Table.CampaignId,
                cfg.Table.ComputerNameColumn,
                cfg.Table.DescriptionColumn,
                cfg.Table.LastScanColumn,
                cfg.Table.OperatorColumn,
                cfg.Table.ResultColumn,
                cfg.Table.TargetDescriptionValue,
                cfg.Table.DoneDescriptionValue,
                cfg.Table.OfflineResultValue,
                cfg.Table.ErrorResultValue,
                cfg.Database.CommandTimeoutSeconds);

            List<BiosScanTarget> targets;

            if (isVerbose)
            {
                if (string.IsNullOrWhiteSpace(computerListPath))
                    throw new InvalidOperationException("Brak ścieżki do pliku computers.txt dla trybu verbose.");

                targets = LoadTargetsFromFile(computerListPath);
                Console.WriteLine($"[INFO] Tryb verbose. Stacje z pliku: {targets.Count}");
                log.Info($"Verbose targets from file '{computerListPath}': {targets.Count}");
            }
            else
            {
                targets = await repo.GetTargetsAsync(
                    cfg.Runner.PERIOD,
                    cfg.Runner.OFFLINE_RETRY_MINUTES,
                    ct); Console.WriteLine($"[INFO] Rekordy do sprawdzenia: {targets.Count}");
                log.Info($"Targets loaded from DB: {targets.Count}");
            }

            if (targets.Count == 0)
            {
                Console.WriteLine("[INFO] Brak rekordów do sprawdzenia.");
                log.Info("Brak rekordów do sprawdzenia.");
                log.Info("=== END CYCLE ===");
                return;
            }

            INetworkDiagnosticService net = new NetworkDiagnosticService();
            var semaphore = new SemaphoreSlim(cfg.Runner.MAX_PARALLEL, cfg.Runner.MAX_PARALLEL);

            int ok = 0, skipped = 0, failed = 0;

            foreach (var batch in targets.Chunk(cfg.Runner.BATCH_SIZE))
            {
                ct.ThrowIfCancellationRequested();

                var names = batch.Select(x => x.ComputerName).ToList();
                var stillEligible = await repo.GetStillEligibleAsync(names, ct);

                var toProcess = batch.Where(x => stillEligible.Contains(x.ComputerName)).ToList();
                foreach (var s in batch.Where(x => !stillEligible.Contains(x.ComputerName)))
                {
                    skipped++;
                    Console.WriteLine($"[SKIP] {s.ComputerName} -> rekord nie jest już 'Do realizacji'.");
                    log.Info($"SKIP {s.ComputerName} -> ITEM_STATE changed manually or item not active/not in campaign");
                }

                if (toProcess.Count == 0)
                    continue;

                var tasks = toProcess.Select(async target =>
                {
                    await semaphore.WaitAsync(ct);
                    try
                    {
                        string computer = target.ComputerName.Trim();
                        DateTime scanTime = DateTime.Now;

                        var diag = await net.IsHostActiveAsync(computer);
                        if (!diag.IsActive)
                        {
                            return new BiosScanUpdate(computer, scanTime, cfg.Table.BotOperatorValue, cfg.Table.OfflineResultValue, false);
                        }

                        string result;
                        try
                        {
                            result = RemoteBiosReader.ReadBiosVersion(computer);
                            if (string.IsNullOrWhiteSpace(result))
                                result = cfg.Table.ErrorResultValue;
                        }
                        catch (Exception ex)
                        {
                            log.Error($"BIOS READ ERROR {computer}: {ex.Message}");
                            result = cfg.Table.ErrorResultValue;
                        }

                        bool markDone = string.Equals(result, cfg.Table.ExpectedSuccessResultValue, StringComparison.OrdinalIgnoreCase);
                        return new BiosScanUpdate(computer, scanTime, cfg.Table.BotOperatorValue, result, markDone);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"TASK ERROR {target.ComputerName}: {ex}");
                        return new BiosScanUpdate(target.ComputerName, DateTime.Now, cfg.Table.BotOperatorValue, cfg.Table.ErrorResultValue, false);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }).ToList();

                var results = await Task.WhenAll(tasks);
                var batchResult = await repo.UpdateBatchAsync(results.ToList(), ct);

                ok += batchResult.Updated;
                skipped += batchResult.SkippedBecauseChanged;
                failed += batchResult.Failed;

                foreach (var u in batchResult.UpdatedItems)
                {
                    Console.WriteLine($"[OK] {u.ComputerName} => {u.ResultValue}");
                    log.Info($"DB UPDATE {u.ComputerName}: LAST_SCAN={u.ScanTime:yyyy-MM-dd HH:mm:ss}, OPERATOR={u.OperatorName}, ITEM_RESULT={u.ResultValue}, DONE={u.MarkDone}");
                }

                foreach (var s in batchResult.SkippedItems)
                {
                    Console.WriteLine($"[SKIP] {s} -> rekord zmieniony ręcznie w międzyczasie");
                    log.Info($"SKIP {s} changed manually in DB");
                }

                foreach (var f in batchResult.FailedItems)
                {
                    Console.WriteLine($"[ERROR] {f.ComputerName} -> {f.Error}");
                    log.Error($"ERROR {f.ComputerName}: {f.Error}");
                }
            }

            Console.WriteLine("== PODSUMOWANIE CYKLU ==");
            Console.WriteLine($"OK: {ok}");
            Console.WriteLine($"Pominięte: {skipped}");
            Console.WriteLine($"Błędy: {failed}");
            log.Info($"SUMMARY CYCLE OK={ok}, SKIP={skipped}, FAIL={failed}");
            log.Info("=== END CYCLE ===");
        }

        private static string ResolveSecureFile(AppConfig cfg, string[] args)
        {
            string secureFile = cfg.Database.SecureConnFile;
            if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) && !args[0].StartsWith("-", StringComparison.OrdinalIgnoreCase))
                secureFile = args[0];

            if (Path.IsPathRooted(secureFile))
                return secureFile;

            string basePath = Path.Combine(AppContext.BaseDirectory, secureFile);
            if (File.Exists(basePath))
                return basePath;

            return Path.GetFullPath(secureFile);
        }

        private static bool TryGetComputerList(string[] args, out string? computerList)
        {
            computerList = null;
            if (args is null || args.Length == 0)
                return false;

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "-verbose", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(args[i], "-computerlist", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        computerList = args[i + 1];
                        return true;
                    }
                }
            }

            return false;
        }

        private static string ResolveFilePath(string fileArg)
        {
            if (Path.IsPathRooted(fileArg))
                return fileArg;

            string fromBaseDirectory = Path.Combine(AppContext.BaseDirectory, fileArg);
            if (File.Exists(fromBaseDirectory))
                return fromBaseDirectory;

            return Path.GetFullPath(fileArg);
        }

        private static List<BiosScanTarget> LoadTargetsFromFile(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Nie znaleziono pliku z listą komputerów: {filePath}");

            return File.ReadAllLines(filePath)
                .Select(x => x?.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Where(x => !x!.StartsWith("#"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(x => new BiosScanTarget(x!))
                .ToList();
        }

        private static AppConfig LoadConfig()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path))
                path = "appsettings.json";

            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return cfg ?? throw new InvalidOperationException("Nie udało się wczytać appsettings.json.");
        }
    }

    internal sealed class AppConfig
    {
        public DatabaseConfig Database { get; set; } = new();
        public TableConfig Table { get; set; } = new();
        public RunnerConfig Runner { get; set; } = new();

        internal sealed class DatabaseConfig
        {
            public string SecureConnFile { get; set; } = "secureconn.dat";
            public int CommandTimeoutSeconds { get; set; } = 60;
            public string? OverrideServer { get; set; } = null;
            public bool? ForceEncrypt { get; set; } = true;
            public bool? ForceTrustServerCertificate { get; set; } = true;
        }

        internal sealed class TableConfig
        {
            public string Schema { get; set; } = "dbo";
            public int CampaignId { get; set; } = 12;
            public string Name { get; set; } = "OHD_CAMPAIGN_ITEMS";
            public string ComputerNameColumn { get; set; } = "COMPUTER_NAME";
            public string DescriptionColumn { get; set; } = "ITEM_STATE";
            public string LastScanColumn { get; set; } = "LAST_SCAN";
            public string OperatorColumn { get; set; } = "OPERATOR";
            public string ResultColumn { get; set; } = "ITEM_RESULT";
            public string TargetDescriptionValue { get; set; } = "Do realizacji";
            public string DoneDescriptionValue { get; set; } = "Zrobione";
            public string OfflineResultValue { get; set; } = "OFFLINE";
            public string ErrorResultValue { get; set; } = "BŁĄD";
            public string BotOperatorValue { get; set; } = "Hades2BIOSVersion";
            public string ExpectedSuccessResultValue { get; set; } = "T74 Ver. 01.23.00";
        }

        internal sealed class RunnerConfig
        {
            public int END_HOUR { get; set; } = 18;
            public int DELAY { get; set; } = 30;
            public int PERIOD { get; set; } = 2;
            public int OFFLINE_RETRY_MINUTES { get; set; } = 30;
            public int BATCH_SIZE { get; set; } = 5;
            public int MAX_PARALLEL { get; set; } = 5;
        }
    }
}
