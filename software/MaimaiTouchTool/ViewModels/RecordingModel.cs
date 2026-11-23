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
    // AddFrame 跑在传输后台线程、Stop 跑在 UI 线程：两者都会动 stream/frameCount，必须互斥。
    private readonly object streamSync = new();
    private long frameCount;
    private DateTimeOffset? startedAt;
    private TimeSpan? finalDuration;
    private bool isRecording;
    private bool recordingFailed;
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

        // 先写进局部变量：文件头写失败（磁盘满等）时立刻释放，否则 stream 字段已被赋值，
        // 下次 Start 直接覆盖它、旧句柄再也没人释放。
        var newStream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try
        {
            var header = new byte[HeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), FormatVersion);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            configSnapshot.Encode().CopyTo(header, 16);
            newStream.Write(header);
        }
        catch
        {
            newStream.Dispose();
            throw;
        }

        lock (streamSync)
        {
            stream = newStream;
            frameCount = 0;
            startedAt = DateTimeOffset.UtcNow;
            finalDuration = null;
            recordingFailed = false;
        }
        FilePath = path;
        IsRecording = true;
        StatusLabel = Localizer.Instance["Rec.StatusRecording"];
        OnPropertyChanged(nameof(FrameCountLabel));
        OnPropertyChanged(nameof(DurationLabel));
    }

    /// <summary>每帧调用；未在录制时是空操作，绝不抛异常（录制故障不能打断遥测流）。</summary>
    public void AddFrame(DebugFrame frame, DateTimeOffset receivedAt)
    {
        bool refreshLabels;
        try
        {
            refreshLabels = WriteRecord(frame, receivedAt);
        }
        catch
        {
            // 磁盘满 / 文件被占用等：停录保流。状态必须标成失败——显示“已完成”会让用户把不完整的文件当成功。
            Stop(failed: true);
            return;
        }
        // 刷新节流：遥测满速 ~33 Hz，每 10 帧刷一次 UI 足够，避免刷屏式通知。
        // AddFrame 在传输后台线程执行，UI 属性通知必须投递回 UI 线程（Avalonia 绑定不允许跨线程写控件）；
        // 投递放在锁外，免得持锁时 UI 线程上的 Stop 还要多等一轮。
        if (refreshLabels)
            Dispatcher.UIThread.Post(() =>
            {
                OnPropertyChanged(nameof(FrameCountLabel));
                OnPropertyChanged(nameof(DurationLabel));
            });
    }

    /// <summary>写一条记录。调用方负责异常处理；返回是否需要刷新 UI 上的帧数/时长。</summary>
    private bool WriteRecord(DebugFrame frame, DateTimeOffset receivedAt)
    {
        lock (streamSync)
        {
            // 与 Stop 互斥：Stop 会先把 stream 摘成 null 再关闭，所以这里必须在锁内重新判定。
            var target = stream;
            if (!IsRecording || target is null) return false;
            var mask = CurrentFieldMask;
            // 扩展字段是真·用户开关：关掉时既不请求固件下发，也把意外到达的扩展段丢掉。
            if (includeExtended && frame.HardwareTouchBits.HasValue) mask |= MaskExtended;
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
            target.Write(record);
            frameCount++;
            return frameCount % 10 == 0;
        }
    }

    public void Stop() => Stop(failed: false);

    /// <param name="failed">写盘/刷新出过错，文件可能不完整——状态不能报“已完成”。</param>
    private void Stop(bool failed)
    {
        FileStream? closing;
        lock (streamSync)
        {
            if (!IsRecording) return;
            isRecording = false;      // 直接写字段：由 PublishState 统一在 UI 线程补通知，避免跨线程抛 PropertyChanged
            closing = stream;
            stream = null;            // 关键：先摘引用。之后即使 Flush 抛异常，句柄也不会泄漏、也不会被重复关闭
            if (startedAt is { } start) finalDuration = DateTimeOffset.UtcNow - start;
            recordingFailed = failed;
        }
        // Flush/Dispose 自己也会失败（拔出 U 盘、磁盘写满）：末条记录确实可能不完整，状态要如实反映。
        try { closing?.Flush(); }
        catch { recordingFailed = true; }
        finally { try { closing?.Dispose(); } catch { recordingFailed = true; } }

        PublishState();
    }

    /// <summary>把状态推给界面。AddFrame 的失败路径在传输线程上调用，必须回到 UI 线程再抛通知。</summary>
    private void PublishState()
    {
        if (Dispatcher.UIThread.CheckAccess()) NotifyState();
        else Dispatcher.UIThread.Post(NotifyState);
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(FrameCountLabel));
        OnPropertyChanged(nameof(DurationLabel));
        OnPropertyChanged(nameof(HasFilePath));
        StatusLabel = BuildStatusLabel();
    }

    /// <summary>按当前状态重建状态文案。语言切换后必须重新格式化——statusLabel 存的是成品字符串，不是模板。</summary>
    private string BuildStatusLabel() =>
        IsRecording ? Localizer.Instance["Rec.StatusRecording"]
        : recordingFailed ? string.Format(Localizer.Instance["Rec.StatusFailed"], frameCount)
        : startedAt is not null ? string.Format(Localizer.Instance["Rec.StatusDone"], frameCount)
        : Localizer.Instance["Rec.StatusIdle"];

    /// <summary>语言切换时由 MainViewModel 调用：重建缓存文案并重抛依赖语言的属性。</summary>
    public void RaiseLocalized()
    {
        StatusLabel = BuildStatusLabel();
        OnPropertyChanged(nameof(FrameCountLabel));
        OnPropertyChanged(nameof(DurationLabel));
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
