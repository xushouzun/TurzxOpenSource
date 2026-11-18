namespace TURZX.WinUsbDisplay;

/// <summary>单个存储卷报告的容量信息。</summary>
/// <param name="TotalKiB">总容量，单位为 KiB。</param>
/// <param name="UsedKiB">已用容量，单位为 KiB。</param>
/// <param name="FreeKiB">可用容量，单位为 KiB。</param>
public readonly record struct StorageVolume(uint TotalKiB, uint UsedKiB, uint FreeKiB);

/// <summary>Win207 固件报告的 SD 卡和内部闪存容量。</summary>
/// <param name="SdCard">SD 卡容量信息。</param>
/// <param name="Flash">内部闪存容量信息。</param>
public readonly record struct StorageCapacity(StorageVolume SdCard, StorageVolume Flash);
