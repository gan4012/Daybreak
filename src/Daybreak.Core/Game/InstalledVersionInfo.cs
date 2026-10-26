namespace Daybreak.Core.Game;

/// <summary>本地已安装版本信息。</summary>
public record InstalledVersionInfo(string Name, long SizeBytes)
{
    public string SizeDisplay => SizeBytes switch
    {
        >= 1_073_741_824 => $"{SizeBytes / 1_073_741_824.0:F2} GB",
        >= 1_048_576 => $"{SizeBytes / 1_048_576.0:F1} MB",
        _ => $"{SizeBytes / 1_024.0:F0} KB",
    };
}
