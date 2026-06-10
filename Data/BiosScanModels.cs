using System;
using System.Collections.Generic;

namespace BiosVersionBot.Data
{
    public record BiosScanTarget(string ComputerName);

    public record BiosScanUpdate(
        string ComputerName,
        DateTime ScanTime,
        string OperatorName,
        string ResultValue,
        bool MarkDone
    );

    public record FailedItem(string ComputerName, string Error);

    public record BatchUpdateResult(
        int Updated,
        int SkippedBecauseChanged,
        int Failed,
        IReadOnlyList<BiosScanUpdate> UpdatedItems,
        IReadOnlyList<string> SkippedItems,
        IReadOnlyList<FailedItem> FailedItems
    );
}
