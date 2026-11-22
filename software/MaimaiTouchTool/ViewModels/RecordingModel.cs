using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using Avalonia.Threading;
using MaimaiTouchTool.Localization;
using MaimaiTouchTool.Protocol;

namespace MaimaiTouchTool.ViewModels;

/// <summary>
/// 调试流录制器：把 STREAMING 模式的帧按选中的字段落盘（字段开关 = 存储层 field_mask，协议帧本身不变）。
/// 文件格式（小端，格式版本 2）：
///   文件头：magic "MMTR" u32 · 格式版本 u16 · 保留 u16 · 开始时刻 UTC Unix 毫秒 s64 · 配置快照 99 字节（applied blob，schema 3）
///   帧记录（可变长）：fieldMask u8 · timestamp_ms u32 · [touch 5B] · [filtered 48×u16] ·
///                    [baseline 48×u8（原始寄存器值，读方 <<2 还原）] · [chip_status 4B · i2c_errors u16] ·
///                    [扩展字段 44B：hw_touch 5B + comp_thresholds 34B + comp_active 5B] ·
///                    主机接收时刻 UTC ticks s64
///   fieldMask 位定义：bit0 touch · bit1 filtered · bit2 baseline · bit3 status · bit4 extended。
///   extended 是用户开关（协议 1.3）：经 STREAM_START 的 field_mask bit4 控制固件是否下发，
///   关掉可显著提升回报率，但失去"硬件判定作为对照答案"的次级数据（对离线算法实验有价值）。
///   timestamp 与主机接收时刻恒记录（丢帧检测/时间轴依赖它们，不可裁）。
/// 配置快照随帧头存一份，离线重算阈值判定（delta = base + sens + electrode）时不需要另存会话配置。
/// </summary>
public sealed class RecordingModel : NotifyModel
{
    public const uint Magic = 0x52544D4D; // "MMTR"
    public const ushort FormatVersion = 2;
    public const int HeaderSize = 4 + 2 + 2 + 8 + ProtocolConstants.ConfigBlobLength;

    public const byte MaskTouch = 0x01;
    public const byte MaskFiltered = 0x02;
    public const byte MaskBaseline = 0x04;
    public const byte MaskStatus = 0x08;
    /// <summary>协议 1.2 扩展帧（251B）携带的字段：hw_touch 5B + comp_thresholds 34B + comp_active 5B = 44B。帧到达即存，非用户开关。</summary>
    public const byte MaskExtended = 0x10;
    public const int ExtendedSize = 5 + 34 + 5;

    private FileStream? stream;
    private long frameCount;
    private DateTimeOffset? startedAt;
    private TimeSpan? finalDuration;
    private bool isRecording;
    private string filePath = string.Empty;
    private string statusLabel;

    // 字段开关（存储层裁剪）：timestamp/主机时刻恒录，其余四组可独立开关。录制中途切换对后续帧立即生效。
    private bool includeTouch = true;
    private bool includeFiltered = true;
    private bool includeBaseline = true;
    private bool includeStatus = true;
    // 扩展字段（硬件触摸/补偿阈值，44B）：关掉可少一个 USB bank 档位，但失去算法实验的次级对照数据。
    private bool includeExtended = true;

    public RecordingModel() => statusLabel = Localizer.Instance["Rec.StatusIdle"];

    public bool IncludeTouch { get => includeTouch; set { includeTouch = value; OnPropertyChanged(); } }
    public bool IncludeFiltered { get => includeFiltered; set { includeFiltered = value; OnPropertyChanged(); } }
    public bool IncludeBaseline { get => includeBaseline; set { includeBaseline = value; OnPropertyChanged(); } }
    public bool IncludeStatus { get => includeStatus; set { includeStatus = value; OnPropertyChanged(); } }
    public bool IncludeExtended { get => includeExtended; set { includeExtended = value; OnPropertyChanged(); } }

    public bool IsRecording { get => isRecording; private set { isRecording = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusLabel)); } }
    public string StatusLabel { get => statusLabel; private set { statusLabel = value; OnPropertyChanged(); } }
    public string FrameCountLabel => string.Format(Localizer.Instance["Rec.Frames"], frameCount);
    // 优先级：录制中走实时时钟；已结束显示冻结的终值；从未开始显示 00:00（startedAt 为 null 时不能拿 default(DateTimeOffset) 去减——那是公元元年的天文数字）
    public string DurationLabel => string.Format(Localizer.Instance["Rec.Duration"],
        startedAt is { } start && finalDuration is null ? FormatDuration(DateTimeOffset.UtcNow - start)
        : finalDuration is { } final ? FormatDuration(final)
        : "00:00");
    public string FilePath { get => filePath; private set { filePath = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasFilePath)); } }
    public bool HasFilePath => File.Exists(filePath);

    public byte CurrentFieldMask =>
        (byte)((includeTouch ? MaskTouch : 0) | (includeFiltered ? MaskFiltered : 0) |
               (includeBaseline ? MaskBaseline : 0) | (includeStatus ? MaskStatus : 0));

    private static string FormatDuration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes:00}:{span.Seconds:00}";

    /// <summary>开始录制。失败（目录/文件打不开）时抛异常，由调用方转成状态消息。</summary>
    public void Start(ConfigBlob configSnapshot)
    {
        if (IsRecording) return;
        var directory = Path.Combine(AppContext.BaseDirectory, "Recordings");
        Directory.CreateDirectory(directory);
        // 同一秒内可能连续开始多次录制（快速停/开、并行测试），重名自动追加序号。
        var path = Path.Combine(directory, $"rec-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.mtr");
        for (var attempt = 2; File.Exists(path); attempt++)
            path = Path.Combine(directory, $"rec-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{attempt}.mtr");

        stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), FormatVersion);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        configSnapshot.Encode().CopyTo(header, 16);
        stream.Write(header);

        frameCount = 0;
        startedAt = DateTimeOffset.UtcNow;
        finalDuration = null;
        FilePath = path;
        IsRecording = true;
        StatusLabel = Localizer.Instance["Rec.StatusRecording"];
        OnPropertyChanged(nameof(FrameCountLabel));
        OnPropertyChanged(nameof(DurationLabel));
    }

    /// <summary>每帧调用；未在录制时是空操作，绝不抛异常（录制故障不能打断遥测流）。</summary>
    public void AddFrame(DebugFrame frame, DateTimeOffset receivedAt)
    {
        if (!IsRecording || stream is null) return;
        try
        {
            var mask = CurrentFieldMask;
            if (frame.HardwareTouchBits.HasValue) mask |= MaskExtended;
            var size = 1 + 4 + 8;
            if ((mask & MaskTouch) != 0) size += 5;
            if ((mask & MaskFiltered) != 0) size += 96;
            if ((mask & MaskBaseline) != 0) size += 48;
            if ((mask & MaskStatus) != 0) size += 6;
            if ((mask & MaskExtended) != 0) size += ExtendedSize;
            var record = new byte[size];
            var offset = 0;
            record[offset++] = mask;
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset), frame.TimestampMs); offset += 4;
            if ((mask & MaskTouch) != 0)
            {
                for (var i = 0; i < 5; i++) record[offset + i] = (byte)(frame.TouchBits >> (8 * i));
                offset += 5;
            }
            if ((mask & MaskFiltered) != 0)
            {
                for (var i = 0; i < 48; i++) BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(offset + i * 2), frame.Filtered[i]);
                offset += 96;
            }
            if ((mask & MaskBaseline) != 0)
            {
                for (var i = 0; i < 48; i++) record[offset + i] = (byte)(frame.Baseline[i] >> 2);
                offset += 48;
            }
            if ((mask & MaskStatus) != 0)
            {
                frame.ChipStatus.CopyTo(record, offset);
                BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(offset + 4), frame.I2cErrors);
                offset += 6;
            }
            if ((mask & MaskExtended) != 0)
            {
                // 协议 1.2 扩展帧布局（DebugFrame.Encode）：hw_touch@207-211 · comp_thresholds@212-245 · comp_active@246-250
                var hw = frame.HardwareTouchBits!.Value;
                for (var i = 0; i < 5; i++) record[offset + i] = (byte)(hw >> (8 * i));
                offset += 5;
                if (frame.CompensationThresholds is { } thresholds)
                {
                    thresholds.CopyTo(record, offset);
                    offset += 34;
                }
                else offset += 34; // 理论上扩展帧必有阈值；缺位时填零占位，保持布局定长
                var active = frame.CompensationActiveBits;
                for (var i = 0; i < 5; i++) record[offset + i] = (byte)(active >> (8 * i));
                offset += 5;
            }
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(^8..), receivedAt.UtcTicks);
            stream.Write(record);
            frameCount++;
            // 刷新节流：遥测满速 ~33 Hz，每 10 帧刷一次 UI 足够，避免刷屏式通知。
            // AddFrame 在传输后台线程执行，UI 属性通知必须投递回 UI 线程（Avalonia 绑定不允许跨线程写控件）。
            if (frameCount % 10 == 0)
                Dispatcher.UIThread.Post(() =>
                {
                    OnPropertyChanged(nameof(FrameCountLabel));
                    OnPropertyChanged(nameof(DurationLabel));
                });
        }
        catch
        {
            // 磁盘满 / 文件被占用等：停录保流，错误状态里能看出帧数不再增长。
            try { Stop(); } catch { }
        }
    }

    public void Stop()
    {
        if (!IsRecording) return;
        IsRecording = false;
        if (startedAt is { } start) finalDuration = DateTimeOffset.UtcNow - start;
        stream?.Flush();
        stream?.Dispose();
        stream = null;
        OnPropertyChanged(nameof(FrameCountLabel));
        OnPropertyChanged(nameof(DurationLabel));
        OnPropertyChanged(nameof(HasFilePath));
        StatusLabel = string.Format(Localizer.Instance["Rec.StatusDone"], frameCount);
    }

    /// <summary>流结束时由 MainViewModel 调用：静默收尾（没在录就什么都不做）。</summary>
    public void StopIfRecording()
    {
        if (IsRecording) Stop();
    }

    public void OpenFolder()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Recordings");
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
    }
}
