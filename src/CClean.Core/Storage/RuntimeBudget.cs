namespace CClean.Core.Storage;

public sealed record RuntimeUsage(long DatabaseBytes, long LogBytes, long TemporaryBytes)
{
    public long TotalBytes => checked(DatabaseBytes + LogBytes + TemporaryBytes);
}

public sealed record BudgetDecision(bool Collect, bool Delete, bool Vacuum, string Reason);

public static class RuntimeBudget
{
    public const long DatabaseLimit = 300L * 1024 * 1024;
    public const long LogLimit = 50L * 1024 * 1024;
    public const long TemporaryLimit = 20L * 1024 * 1024;
    public const long TotalLimit = 400L * 1024 * 1024;
    public const long EmergencyFreeBytes = 1024L * 1024 * 1024;

    public static RuntimeUsage Measure(string root)
    {
        long database = 0, logs = 0, temporary = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Runtime data link refused.");
            var bytes = new FileInfo(path).Length;
            var name = Path.GetFileName(path);
            if (name.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || name.Contains(".db-", StringComparison.OrdinalIgnoreCase)) database = checked(database + bytes);
            else if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) logs = checked(logs + bytes);
            else temporary = checked(temporary + bytes);
        }
        return new(database, logs, temporary);
    }

    public static BudgetDecision Evaluate(RuntimeUsage usage, long userFreeBytes, bool auditWritable, long nextBatchBytes = 0)
    {
        if (usage.DatabaseBytes < 0 || usage.LogBytes < 0 || usage.TemporaryBytes < 0 || nextBatchBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(usage));
        var pressured = nextBatchBytes >= DatabaseLimit * 9 / 10 - usage.DatabaseBytes ||
            usage.LogBytes >= LogLimit * 9 / 10 || usage.TemporaryBytes >= TemporaryLimit * 9 / 10 ||
            nextBatchBytes >= TotalLimit * 9 / 10 - usage.TotalBytes;
        var low = userFreeBytes < EmergencyFreeBytes;
        return new(!pressured && !low, auditWritable && !pressured && !low,
            !pressured && !low, !auditWritable ? "Audit unavailable" : low ? "Low free space" : pressured ? "Runtime budget reached" : "Normal");
    }
}
