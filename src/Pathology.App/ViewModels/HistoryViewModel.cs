using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pathology.App.Repair;
using Pathology.App.Scanning;
using Pathology.Core.Model;
using Pathology.Core.Remediation;
using static Pathology.App.Theme;

namespace Pathology.App.ViewModels;

/// <summary>
/// The History page: every apply, newest first, with what it changed (the backup), what happened to each write,
/// and Undo. Undo is itself an apply, so it's recorded here and can be undone in turn.
/// </summary>
public sealed partial class HistoryViewModel : PageViewModel
{
    readonly IRepairService _repair;
    readonly ScanSession _session;

    public HistoryViewModel(IRepairService repair, ScanSession session)
    {
        _repair = repair;
        _session = session;
        Reload();
    }

    public override string Title => "History";

    [ObservableProperty] private IReadOnlyList<HistoryRowViewModel> _rows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private HistoryRowViewModel? _selected;

    [ObservableProperty] private HistoryDetailViewModel? _detail;

    public bool HasSelection => Selected is not null;
    public bool IsEmpty => Rows.Count == 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand), nameof(ConfirmUndoCommand))]
    private bool _isConfirming;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand), nameof(ConfirmUndoCommand))]
    private bool _isUndoing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _status = "";

    [ObservableProperty] private bool _statusFailed;

    public bool HasStatus => Status.Length > 0;

    protected override void OnActivated() => Reload();

    /// <summary>Read the records again, keeping the selection.</summary>
    public void Reload()
    {
        var keep = Selected?.Record.Id;
        IReadOnlyList<ChangeRecord> records;
        try { records = _repair.History(); }
        catch (Exception) { records = []; }
        Rows = records.Select(r => new HistoryRowViewModel(r, this)).ToList();
        Selected = Rows.FirstOrDefault(r => r.Record.Id == keep) ?? Rows.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    internal void Select(HistoryRowViewModel row) => Selected = row;

    partial void OnSelectedChanged(HistoryRowViewModel? oldValue, HistoryRowViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        Detail = newValue is null ? null : new HistoryDetailViewModel(newValue.Record);
        IsConfirming = false;
        UndoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => IsConfirming = true;

    bool CanUndo() => Selected?.Record.CanUndo == true && !IsConfirming && !IsUndoing;

    [RelayCommand]
    private void CancelUndo() => IsConfirming = false;

    /// <summary>Put back what the selected change changed: the one path from this page to the writers.</summary>
    [RelayCommand(CanExecute = nameof(CanConfirmUndo))]
    private async Task ConfirmUndo()
    {
        if (Selected?.Record is not { } record) return;
        IsUndoing = true;
        try
        {
            var outcome = await AppServices.RunAsync(() =>
            {
                var undo = _repair.Undo(record);
                return undo.IsEmpty ? null : _repair.Apply(undo, $"Undo of {record.Summary}", [], record.Id);
            });
            if (outcome is null)
            {
                StatusFailed = true;
                Status = "There's nothing left to put back.";
            }
            else
            {
                StatusFailed = outcome.Outcome is not ChangeOutcome.Applied;
                Status = Outcomes.Describe(outcome);
                Reload();
                if (outcome.Applied.Any()) await _session.ScanAsync();
            }
        }
        catch (Exception ex)
        {
            StatusFailed = true;
            Status = $"Nothing was undone: {ex.Message}";
        }
        finally
        {
            IsUndoing = false;
            IsConfirming = false;
        }
    }

    bool CanConfirmUndo() => IsConfirming && !IsUndoing;
}

/// <summary>One apply in the list.</summary>
public sealed partial class HistoryRowViewModel(ChangeRecord record, HistoryViewModel owner) : ViewModelBase
{
    public ChangeRecord Record { get; } = record;

    [ObservableProperty] private bool _isSelected;

    public string Summary => Record.Summary;
    public string When => Record.StartedAt.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture);
    public string OutcomeWord => HistoryDetailViewModel.Word(Record);
    public IBrush OutcomeBrush => HistoryDetailViewModel.BrushOf(Record);

    [RelayCommand]
    private void Open() => owner.Select(this);
}

/// <summary>Everything one record holds.</summary>
public sealed class HistoryDetailViewModel
{
    public HistoryDetailViewModel(ChangeRecord record)
    {
        Record = record;
        Heading = record.Summary;
        When = record.StartedAt.ToLocalTime().ToString("dddd d MMMM yyyy, HH:mm:ss", CultureInfo.CurrentCulture);
        OutcomeWord = Word(record);
        OutcomeBrush = BrushOf(record);
        Message = record.Message ?? "";
        Fixes = record.Fixes;
        Values = record.Changes.Values.Select(v => new ValueRecordViewModel(v)).ToList();
        Acls = record.Changes.Acls.Where(a => a.Writes).Select(a => new AclChangeViewModel(a)).ToList();
        Steps = record.Steps.Select(s => new StepRowViewModel(s)).ToList();
        UndoNote = record.UndoneBy is not null ? "This change has been undone."
            : record.CanUndo ? "Undo puts back what this change wrote: each PATH value and each folder's permissions as they were before it."
            : "Nothing of this change was written, so there's nothing to undo.";
    }

    public ChangeRecord Record { get; }
    public string Heading { get; }
    public string When { get; }
    public string OutcomeWord { get; }
    public IBrush OutcomeBrush { get; }
    public string Message { get; }
    public bool HasMessage => Message.Length > 0;
    public IReadOnlyList<string> Fixes { get; }
    public bool HasFixes => Fixes.Count > 0;
    public IReadOnlyList<ValueRecordViewModel> Values { get; }
    public bool HasValues => Values.Count > 0;
    public IReadOnlyList<AclChangeViewModel> Acls { get; }
    public bool HasAcls => Acls.Count > 0;
    public IReadOnlyList<StepRowViewModel> Steps { get; }
    public bool HasSteps => Steps.Count > 0;
    public string UndoNote { get; }
    public bool IsUndo => Record.UndoOf is not null;

    internal static string Word(ChangeRecord record) => record.UndoneBy is not null ? "UNDONE" : record.Outcome switch
    {
        ChangeOutcome.Applied => record.UndoOf is null ? "APPLIED" : "UNDO",
        ChangeOutcome.Partial => "PARTLY APPLIED",
        ChangeOutcome.Cancelled => "CANCELLED",
        ChangeOutcome.Refused => "REFUSED",
        ChangeOutcome.Pending => "INTERRUPTED",
        _ => "FAILED",
    };

    internal static IBrush BrushOf(ChangeRecord record) => record.UndoneBy is not null ? Brush("MutedBrush") : record.Outcome switch
    {
        ChangeOutcome.Applied => Brush("OkBrush"),
        ChangeOutcome.Partial or ChangeOutcome.Pending => Brush("WarnBrush"),
        ChangeOutcome.Cancelled or ChangeOutcome.Refused => Brush("MutedBrush"),
        _ => Brush("DangerBrush"),
    };
}

/// <summary>A PATH value as a record keeps it: before and after, verbatim.</summary>
public sealed class ValueRecordViewModel(ValueChange change)
{
    public string Heading { get; } = change.Scope == PathScope.Machine ? "SYSTEM PATH" : "USER PATH";
    public string Before { get; } = Text(change.Before);
    public string After { get; } = Text(change.After);

    static string Text(RawPathValue value) => value.Kind switch
    {
        PathValueKind.Missing => "(not set)",
        _ => $"{(value.Kind == PathValueKind.ExpandString ? "REG_EXPAND_SZ" : "REG_SZ")}: {value.Value}",
    };
}
