namespace WinStatusBar.Core.Ai;

/// <summary>Completion needs fresh observations after generation stops, not just a quiet stream.</summary>
public sealed class CopilotResponseState
{
    private string _text = "";
    private TimeSpan _stableSince;
    private int _samples;
    private bool _wasGenerating;
    public bool Observe(string text, bool generating, TimeSpan now)
    {
        if (generating || _wasGenerating || string.IsNullOrWhiteSpace(text) || text != _text)
        { _text = text; _stableSince = now; _samples = 0; }
        else _samples++;
        _wasGenerating = generating;
        return !generating && !string.IsNullOrWhiteSpace(text) && _samples >= 2 && now - _stableSince >= TimeSpan.FromSeconds(3);
    }
}
