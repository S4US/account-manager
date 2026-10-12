namespace AccountManager.Core.Updates;

/// <summary>
/// Swaps the running exe for a downloaded one. Windows lets a running exe be renamed but not overwritten, so the
/// current exe is moved aside to "*.old", the new one copied into its place, and the old copy deleted by the next
/// start (<see cref="CleanUpPrevious"/>).
/// </summary>
public static class UpdateInstaller
{
    private const string OldSuffix = ".old";

    /// <summary>
    /// Replaces <paramref name="currentExe"/> with <paramref name="newExe"/>. On failure the original is put back
    /// and an <see cref="UpdateException"/> explains why.
    /// </summary>
    public static void Install(string currentExe, string newExe)
    {
        string old = currentExe + OldSuffix;
        TryDelete(old);

        try
        {
            File.Move(currentExe, old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException(
                "The app can't replace itself in this folder. Move it somewhere you can write to (not Program Files), or download the new version from GitHub.", ex);
        }

        try
        {
            File.Copy(newExe, currentExe, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Put the original back so the app still starts.
            try
            {
                File.Move(old, currentExe, overwrite: true);
            }
            catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
            {
                // If even this fails, "*.old" is still there for the user to rename by hand.
            }

            throw new UpdateException("Couldn't put the new version in place: " + ex.Message, ex);
        }
    }

    /// <summary>Deletes the previous version left behind by an update. Safe to call on every start.</summary>
    public static void CleanUpPrevious(string currentExe)
    {
        string old = currentExe + OldSuffix;
        for (int attempt = 0; attempt < 10 && File.Exists(old); attempt++)
        {
            // The previous copy may still be closing.
            if (TryDelete(old))
            {
                return;
            }

            Thread.Sleep(300);
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
