using System.ComponentModel;
namespace WinStatusBar.Views;
public sealed partial class BarWindow
{
    private ContourGlow? _agentContour;
    private void SetupAgentGlow()
    {
        _agentContour = new ContourGlow(AgentModule, 18f);
        VM.PropertyChanged += OnGlowVmChanged;
        UpdateAgentGlow(true);
    }
    private void OnGlowVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VM.AgentActive)) DispatcherQueue.TryEnqueue(() => UpdateAgentGlow(false));
    }
    private void TeardownAgentGlow() { VM.PropertyChanged -= OnGlowVmChanged; _agentContour?.SetActive(false); }
    private void UpdateGlowColors() => _agentContour?.UpdateColors();
    private void UpdateAgentGlow(bool restart)
    {
        if (_agentContour is null) return;
        _agentContour.Enabled = _settings.Current.AgentGlow;
        _agentContour.SetActive(VM.AgentActive);
    }
}
