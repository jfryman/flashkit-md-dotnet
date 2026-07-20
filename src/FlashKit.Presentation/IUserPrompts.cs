namespace FlashKit.Presentation;

public enum PromptFileKind { RomImage, SaveRam, IpsPatch }

/// <summary>
/// Decisions the presentation model must ask the user for. Each front-end
/// implements these with its native affordances (Avalonia StorageProvider
/// pickers and dialogs, Terminal.Gui FileDialog, ...). Returning null (or
/// false for the confirmation) means the user cancelled.
/// </summary>
public interface IUserPrompts
{
    Task<string?> PickSavePath(string suggestedName, PromptFileKind kind);
    Task<string?> PickOpenPath(PromptFileKind kind);
    Task<string?> PickFolder();
    Task<bool> ConfirmAutoWrite();
}
