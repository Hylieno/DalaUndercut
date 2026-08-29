using Dalamud.Configuration;

namespace DalaLenoUndercut;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;
    public int UndercutAmount { get; set; } = 1;
    public int MinimumPrice { get; set; } = 1;
    public int ActionDelayMs { get; set; } = 1000;
    public bool IgnoreOwnRetainers { get; set; } = true;
    public bool OpenDiagnosticWindowAutomatically { get; set; } = true;
}
