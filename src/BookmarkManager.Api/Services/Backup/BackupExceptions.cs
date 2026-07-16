namespace BookmarkManager.Api.Services.Backup;

public sealed class BackupAlreadyRunningException : Exception
{
    public BackupAlreadyRunningException()
        : base("A backup is already in progress.")
    {
    }
}

public sealed class BackupNotFoundException : Exception
{
    public BackupNotFoundException()
        : base("Backup not found.")
    {
    }
}

public sealed class BackupInvalidConfirmException : Exception
{
    public BackupInvalidConfirmException()
        : base("Confirmation value must be exactly 'RESTORE'.")
    {
    }
}

public sealed class BackupRestoreException : Exception
{
    public BackupRestoreException(string message)
        : base(message)
    {
    }

    public BackupRestoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
