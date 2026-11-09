namespace WinStatusBar.ViewModels;

public sealed class ModuleOption : ObservableObject
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    private bool? _isShown;
    public override string ToString() => Title;
    public bool? IsShown { get => _isShown; set => Set(ref _isShown, value); }
}
