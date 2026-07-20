using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FlashKit.Presentation;

/// <summary>One row in the transaction log: a READ/WRITE of ROM/RAM with its
/// own progress bar and final status. Mutated on the UI thread only (worker
/// progress reports are marshalled by Progress&lt;T&gt;).</summary>
public sealed class TransactionEntry : INotifyPropertyChanged
{
    public string Time { get; } = DateTime.Now.ToString("HH:mm:ss");
    public string Title { get; }

    string detail = "";
    string status = "";
    double progressMax = 1;
    double progressValue;
    bool failed;
    bool succeeded;
    bool running = true;

    public TransactionEntry(string title) => Title = title;

    public string Detail
    {
        get => detail;
        set { Set(ref detail, value); Raise(nameof(HasDetail)); }
    }

    public bool HasDetail => detail.Length != 0;
    public string Status { get => status; set => Set(ref status, value); }
    public double ProgressMax { get => progressMax; set => Set(ref progressMax, value); }
    public double ProgressValue { get => progressValue; set => Set(ref progressValue, value); }
    public bool Failed { get => failed; private set => Set(ref failed, value); }
    public bool Succeeded { get => succeeded; private set => Set(ref succeeded, value); }
    public bool Running { get => running; private set => Set(ref running, value); }

    public void Succeed(string message)
    {
        Status = message;
        ProgressValue = ProgressMax;
        Succeeded = true;
        Running = false;
    }

    public void Fail(string message)
    {
        Status = message;
        Failed = true;
        Running = false;
    }

    public void Cancel()
    {
        Status = "Cancelled";
        Running = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }

    void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
