using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BiosVersionBot.Data
{
    public sealed class BiosScanRepository
    {
        private readonly string _connectionString;
        private readonly string _schema;
        private readonly string _table;
        private readonly int _campaignId;
        private readonly string _colComputer;
        private readonly string _colDescription;
        private readonly string _colLastScan;
        private readonly string _colOperator;
        private readonly string _colResult;
        private readonly string _targetDescriptionValue;
        private readonly string _doneDescriptionValue;
        private readonly string _offlineResultValue;
        private readonly string _errorResultValue;
        private readonly int _commandTimeoutSeconds;

        public BiosScanRepository(
            string connectionString,
            string schema,
            string table,
            int campaignId,
            string colComputer,
            string colDescription,
            string colLastScan,
            string colOperator,
            string colResult,
            string targetDescriptionValue,
            string doneDescriptionValue,
            string offlineResultValue,
            string errorResultValue,
            int commandTimeoutSeconds)
        {
            _connectionString = connectionString;
            _schema = schema;
            _table = table;
            _campaignId = campaignId;
            _colComputer = colComputer;
            _colDescription = colDescription;
            _colLastScan = colLastScan;
            _colOperator = colOperator;
            _colResult = colResult;
            _targetDescriptionValue = targetDescriptionValue;
            _doneDescriptionValue = doneDescriptionValue;
            _offlineResultValue = offlineResultValue;
            _errorResultValue = errorResultValue;
            _commandTimeoutSeconds = commandTimeoutSeconds;
        }

        private string FullTable => $"[{_schema}].[{_table}]";
        private string FullHistoryTable => $"[{_schema}].[OHD_CAMPAIGN_ITEM_HISTORY]";
        private string FullItemsView => $"[{_schema}].[v_OHD_CAMPAIGN_ITEMS_FULL]";

        public async Task<List<BiosScanTarget>> GetTargetsAsync(
            int periodHours,
            int offlineRetryMinutes,
            CancellationToken ct)
        {
            string sql = $@"
WITH q AS (
    SELECT
        CAST(t.[{_colComputer}] AS nvarchar(256)) AS ComputerName,
        ROW_NUMBER() OVER (
            PARTITION BY CAST(t.[{_colComputer}] AS nvarchar(256))
            ORDER BY t.ITEM_ID
        ) AS rn
    FROM {FullTable} t
    WHERE
        t.CAMPAIGN_ID = @campaignId
        AND t.IS_ACTIVE = 1
        AND NULLIF(LTRIM(RTRIM(t.[{_colComputer}])), '') IS NOT NULL
        AND t.[{_colDescription}] = @targetDescription
        AND
        (
            (
                t.[{_colResult}] = @offlineResult
                AND (
                    t.UPDATED_AT IS NULL
                    OR t.UPDATED_AT < DATEADD(MINUTE, -@offlineRetryMinutes, GETDATE())
                )
            )
            OR
            (
                ISNULL(t.[{_colResult}], '') <> @offlineResult
                AND (
                    t.[{_colLastScan}] IS NULL
                    OR t.[{_colLastScan}] < DATEADD(HOUR, -@periodHours, GETDATE())
                )
            )
        )
)
SELECT ComputerName
FROM q
WHERE rn = 1
ORDER BY ComputerName;";

            var result = new List<BiosScanTarget>();

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = _commandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@campaignId", _campaignId);
            cmd.Parameters.AddWithValue("@targetDescription", _targetDescriptionValue);
            cmd.Parameters.AddWithValue("@offlineResult", _offlineResultValue);
            cmd.Parameters.AddWithValue("@periodHours", periodHours);
            cmd.Parameters.AddWithValue("@offlineRetryMinutes", offlineRetryMinutes);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var computer = reader.IsDBNull(0) ? "" : reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(computer))
                    result.Add(new BiosScanTarget(computer.Trim()));
            }

            return result;
        }

        public async Task<HashSet<string>> GetStillEligibleAsync(List<string> computerNames, CancellationToken ct)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (computerNames.Count == 0)
                return set;

            var dt = new DataTable();
            dt.Columns.Add("ComputerName", typeof(string));

            foreach (var c in computerNames.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                dt.Rows.Add(c.Trim());

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            using var tx = conn.BeginTransaction();

            using (var create = new SqlCommand("CREATE TABLE #names(ComputerName nvarchar(256) NOT NULL);", conn, tx))
            {
                create.CommandTimeout = _commandTimeoutSeconds;
                await create.ExecuteNonQueryAsync(ct);
            }

            using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx))
            {
                bulk.DestinationTableName = "#names";
                bulk.ColumnMappings.Add("ComputerName", "ComputerName");
                await bulk.WriteToServerAsync(dt, ct);
            }

            string sql = $@"
SELECT DISTINCT CAST(t.[{_colComputer}] AS nvarchar(256)) AS ComputerName
FROM {FullTable} t
JOIN #names n ON CAST(t.[{_colComputer}] AS nvarchar(256)) = n.ComputerName
WHERE
    t.CAMPAIGN_ID = @campaignId
    AND t.IS_ACTIVE = 1
    AND NULLIF(LTRIM(RTRIM(t.[{_colComputer}])), '') IS NOT NULL
    AND t.[{_colDescription}] = @targetDescription;";

            using (var cmd = new SqlCommand(sql, conn, tx))
            {
                cmd.CommandTimeout = _commandTimeoutSeconds;
                cmd.Parameters.AddWithValue("@campaignId", _campaignId);
                cmd.Parameters.AddWithValue("@targetDescription", _targetDescriptionValue);

                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    if (!reader.IsDBNull(0))
                        set.Add(reader.GetString(0).Trim());
                }
            }

            await tx.CommitAsync(ct);
            return set;
        }

        public async Task<BatchUpdateResult> UpdateBatchAsync(List<BiosScanUpdate> updates, CancellationToken ct)
        {
            updates ??= new List<BiosScanUpdate>();

            var updated = new List<BiosScanUpdate>();
            var skipped = new List<string>();
            var failed = new List<FailedItem>();

            if (updates.Count == 0)
                return new BatchUpdateResult(0, 0, 0, updated, skipped, failed);

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            using var tx = conn.BeginTransaction();

            try
            {
                foreach (var u in updates)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        bool isOffline = string.Equals(u.ResultValue, _offlineResultValue, StringComparison.OrdinalIgnoreCase);

                        var newState = u.MarkDone ? _doneDescriptionValue : _targetDescriptionValue;

                        string sql = $@"
DECLARE @ItemId INT;
DECLARE @OldState NVARCHAR(50);
DECLARE @OldResult NVARCHAR(1000);
DECLARE @OldOperator NVARCHAR(100);
DECLARE @RowsAffected INT;

SELECT TOP (1)
    @ItemId = ITEM_ID,
    @OldState = [{_colDescription}],
    @OldResult = [{_colResult}],
    @OldOperator = [{_colOperator}]
FROM {FullTable}
WHERE CAMPAIGN_ID = @CampaignId
  AND [{_colComputer}] = @ComputerName
  AND IS_ACTIVE = 1
ORDER BY ITEM_ID;

SET @RowsAffected = 0;

IF @ItemId IS NOT NULL
BEGIN
    UPDATE {FullTable}
    SET
        [{_colDescription}] = @NewState,
        [{_colResult}] = @NewResult,
        [{_colOperator}] = @Operator,
        UPDATED_AT = GETDATE(),

        -- OFFLINE nie ustawia LAST_SCAN, żeby nie blokować kolejnej próby na PERIOD godzin.
        [{_colLastScan}] = CASE
            WHEN @IsOffline = 1 THEN [{_colLastScan}]
            ELSE @LastScan
        END,

        CLOSED_AT = CASE
            WHEN @NewState = @DoneState THEN GETDATE()
            ELSE CLOSED_AT
        END
    WHERE ITEM_ID = @ItemId
      AND [{_colDescription}] = @TargetState;

    SET @RowsAffected = @@ROWCOUNT;

    -- Historia tylko przy faktycznej zmianie stanu na Zrobione.
    IF @RowsAffected > 0 AND @OldState <> @NewState AND @NewState = @DoneState
    BEGIN
        INSERT INTO {FullHistoryTable} (
            ITEM_ID, EVENT_TYPE, OLD_STATE, NEW_STATE, OLD_RESULT, NEW_RESULT,
            OLD_OPERATOR, NEW_OPERATOR, CHANGED_BY, CHANGED_AT, COMMENT,
            CTX_COMPUTER_NAME, CTX_SAP_COMPANY_CODE, CTX_SAP_SAT,
            CTX_SAP_USER_NAME, CTX_SAP_DEPARTMENT_NAME, CTX_SAP_ROOM,
            CTX_SAP_UNIT, CTX_SAP_DEPARTMENT_CODE, CTX_SAP_MODEL,
            CTX_SAP_PRODUCER, CTX_DC_COMPUTER_NAME
        )
        SELECT
            @ItemId,
            'STATE_CHANGED',
            @OldState,
            @NewState,
            @OldResult,
            @NewResult,
            @OldOperator,
            @Operator,
            @Operator,
            GETDATE(),
            N'Automatyczny odczyt wersji BIOS przez BiosVersionBot.',
            v.COMPUTER_NAME,
            v.CURRENT_SAP_COMPANY_CODE,
            v.CURRENT_SAP_SAT,
            v.CURRENT_SAP_USER_NAME,
            v.CURRENT_SAP_DEPARTMENT_NAME,
            v.CURRENT_SAP_ROOM,
            v.CURRENT_SAP_UNIT,
            v.CURRENT_SAP_DEPARTMENT_CODE,
            v.CURRENT_SAP_MODEL,
            v.CURRENT_SAP_PRODUCER,
            v.CURRENT_DC_COMPUTER_NAME
        FROM {FullItemsView} v
        WHERE v.ITEM_ID = @ItemId;
    END
END

SELECT @RowsAffected;";

                        using var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = _commandTimeoutSeconds };
                        cmd.Parameters.AddWithValue("@CampaignId", _campaignId);
                        cmd.Parameters.AddWithValue("@ComputerName", u.ComputerName);
                        cmd.Parameters.AddWithValue("@NewState", newState);
                        cmd.Parameters.AddWithValue("@NewResult", u.ResultValue);
                        cmd.Parameters.AddWithValue("@Operator", u.OperatorName);
                        cmd.Parameters.AddWithValue("@LastScan", u.ScanTime);
                        cmd.Parameters.AddWithValue("@DoneState", _doneDescriptionValue);
                        cmd.Parameters.AddWithValue("@TargetState", _targetDescriptionValue);
                        cmd.Parameters.Add("@IsOffline", SqlDbType.Bit).Value = isOffline;

                        var obj = await cmd.ExecuteScalarAsync(ct);
                        int rows = Convert.ToInt32(obj);

                        if (rows > 0)
                            updated.Add(u);
                        else
                            skipped.Add(u.ComputerName);
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new FailedItem(u.ComputerName, ex.Message));
                    }
                }

                await tx.CommitAsync(ct);
                return new BatchUpdateResult(updated.Count, skipped.Count, failed.Count, updated, skipped, failed);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);

                foreach (var u in updates)
                    failed.Add(new FailedItem(u.ComputerName, ex.Message));

                return new BatchUpdateResult(
                    0,
                    0,
                    failed.Count,
                    Array.Empty<BiosScanUpdate>(),
                    Array.Empty<string>(),
                    failed);
            }
        }
    }
}