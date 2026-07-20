namespace FlashKit.Presentation;

/// <summary>
/// The destructive-action warning shown before enabling auto-write, and its
/// "don't show again" persistence. The wording lives here so every front-end
/// warns with the same words.
/// </summary>
public static class AutoWriteWarning
{
    public const string Title = "Enable auto-write?";

    public const string Text =
        "Auto-write ERASES and reprograms every flash cartridge "
        + "inserted while it is enabled, without asking again.\n\n"
        + "Cartridges without a writable flash chip (retail game "
        + "carts) are detected and skipped, but the contents of any "
        + "flash cart you insert will be destroyed and replaced "
        + "with the chosen file.";

    static string SuppressPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "flashkit-md", "suppress-auto-write-warning");

    public static bool Suppressed => File.Exists(SuppressPath);

    public static void Suppress()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SuppressPath)!);
            File.WriteAllText(SuppressPath, "");
        }
        catch (Exception) { }
    }
}
