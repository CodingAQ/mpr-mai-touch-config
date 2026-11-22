using System.Buffers.Binary;
using MaimaiTouchTool.Protocol;
using MaimaiTouchTool.ViewModels;
using Xunit;

namespace MaimaiTouchTool.Tests;

public class RecordingTests
{
    private static DebugFrame SampleFrame() => new(
        1234,
        0x3,
        Enumerable.Range(0, 48).Select(i => (ushort)(100 + i)).ToArray(),
        Enumerable.Range(0, 48).Select(i => (ushort)(200 + i * 4)).ToArray(), // 全部是 4 的倍数
        [7, 7, 7, 7],
        42);

    [Fact]
    public void RecordingWritesHeaderAndFullFrames()
    {
        var recorder = new RecordingModel();
        var config = ConfigBlob.Defaults();
        var frame = SampleFrame();
        var received = new DateTimeOffset(2026, 11, 4, 3, 0, 0, TimeSpan.Zero);

        recorder.Start(config);
        recorder.AddFrame(frame, received);
        recorder.AddFrame(frame, received.AddMilliseconds(30));
        recorder.Stop();

        Assert.True(recorder.HasFilePath);
        var bytes = File.ReadAllBytes(recorder.FilePath);
        // 全开掩码：1 mask + 4 ts + 5 touch + 96 filtered + 48 baseline + 6 status + 8 ticks = 168
        var recordSize = 1 + 4 + 5 + 96 + 48 + 6 + 8;
        Assert.Equal(RecordingModel.HeaderSize + 2 * recordSize, bytes.Length);

        // 文件头：魔数、格式版本、开始时刻、93 字节配置快照
        Assert.Equal(RecordingModel.Magic, BitConverter.ToUInt32(bytes, 0));
        Assert.Equal(RecordingModel.FormatVersion, BitConverter.ToUInt16(bytes, 4));
        Assert.True(BitConverter.ToInt64(bytes, 8) <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal(config.Encode(), bytes[16..(16 + ProtocolConstants.ConfigBlobLength)]);

        // 逐条记录布局：mask@0(1) · ts@1(4) · touch@5(5) · filtered@10(96) · baseline@106(48) · status@154(4+2) · ticks@160(8)
        for (var index = 0; index < 2; index++)
        {
            var record = bytes.AsSpan((RecordingModel.HeaderSize + index * recordSize)..);
            Assert.Equal(RecordingModel.MaskTouch | RecordingModel.MaskFiltered | RecordingModel.MaskBaseline | RecordingModel.MaskStatus, record[0]);
            Assert.Equal(frame.TimestampMs, BinaryPrimitives.ReadUInt32LittleEndian(record[1..]));
            Assert.Equal(frame.Encode().AsSpan(4, 5 + 96).ToArray(), record.Slice(5, 5 + 96).ToArray()); // touch + filtered 与协议负载逐位一致
            for (var i = 0; i < 48; i++) Assert.Equal((byte)(frame.Baseline[i] >> 2), record[106 + i]);
            Assert.Equal(frame.ChipStatus, record.Slice(154, 4).ToArray());
            Assert.Equal(frame.I2cErrors, BinaryPrimitives.ReadUInt16LittleEndian(record[158..]));
            Assert.Equal(received.AddMilliseconds(30 * index).UtcTicks, BitConverter.ToInt64(record[160..]));
        }
    }

    [Fact]
    public void FieldTogglesShrinkRecords()
    {
        var recorder = new RecordingModel { IncludeTouch = false, IncludeBaseline = false, IncludeStatus = false };
        var frame = SampleFrame();
        var received = DateTimeOffset.UtcNow;

        recorder.Start(ConfigBlob.Defaults());
        recorder.AddFrame(frame, received);
        recorder.Stop();

        var bytes = File.ReadAllBytes(recorder.FilePath);
        // 只留 filtered：1 mask + 4 ts + 96 filtered + 8 ticks = 109
        Assert.Equal(RecordingModel.HeaderSize + 109, bytes.Length);
        var record = bytes.AsSpan(RecordingModel.HeaderSize..);
        Assert.Equal(RecordingModel.MaskFiltered, record[0]);
        for (var i = 0; i < 48; i++)
            Assert.Equal(frame.Filtered[i], BinaryPrimitives.ReadUInt16LittleEndian(record[(5 + i * 2)..]));
        Assert.Equal(received.UtcTicks, BitConverter.ToInt64(record[101..]));
    }

    [Fact]
    public void ExtendedFrameFieldsAreRecorded()
    {
        var recorder = new RecordingModel { IncludeTouch = false, IncludeFiltered = false, IncludeBaseline = false, IncludeStatus = false };
        var frame = new DebugFrame(
            999, 0x3, new ushort[48], new ushort[48], [7, 7, 7, 7], 0,
            HardwareTouchBits: 0x1F, // 触发 251B 扩展帧
            CompensationThresholds: Enumerable.Range(0, 34).Select(i => (byte)(i * 2)).ToArray(),
            CompensationActiveBits: 0x15);
        var received = DateTimeOffset.UtcNow;

        recorder.Start(ConfigBlob.Defaults());
        recorder.AddFrame(frame, received);
        recorder.Stop();

        var bytes = File.ReadAllBytes(recorder.FilePath);
        // mask@0 + ts@1(4) + hw_touch@5(5) + comp_thresholds@10(34) + comp_active@44(5) + ticks@49(8) = 57
        Assert.Equal(RecordingModel.HeaderSize + 57, bytes.Length);
        var record = bytes.AsSpan(RecordingModel.HeaderSize..);
        Assert.Equal(RecordingModel.MaskExtended, record[0]);
        Assert.Equal(999u, BinaryPrimitives.ReadUInt32LittleEndian(record[1..]));
        ulong hw = 0, active = 0;
        for (var i = 0; i < 5; i++) { hw |= (ulong)record[5 + i] << (8 * i); active |= (ulong)record[44 + i] << (8 * i); }
        Assert.Equal(frame.HardwareTouchBits!.Value, hw);
        for (var i = 0; i < 34; i++) Assert.Equal((byte)(i * 2), record[10 + i]);
        Assert.Equal(frame.CompensationActiveBits, active);
        Assert.Equal(received.UtcTicks, BitConverter.ToInt64(record[49..]));
    }

    [Fact]
    public void AddFrameWithoutStartIsNoOp()
    {
        var recorder = new RecordingModel();
        var frame = new DebugFrame(0, 0, new ushort[48], new ushort[48], new byte[4], 0);
        recorder.AddFrame(frame, DateTimeOffset.UtcNow); // 不应抛异常
        recorder.StopIfRecording(); // 未在录制时也应静默
        Assert.False(recorder.IsRecording);
    }
}

/// <summary>0x91 掩码帧解码（协议 1.3）：逐字段裁剪组合与字节布局对齐固件 encodeMaskedDebugPayload。</summary>
public class MaskedDebugFrameTests
{
    private static byte[] BuildPayload(byte mask, out uint timestamp, out ulong touch, out ushort[] filtered,
        out ushort[] baseline, out byte[] status, out ushort i2c, out ulong hw, out byte[] thresholds, out ulong active)
    {
        timestamp = 0x11223344; touch = 0xABC; hw = 0x123; active = 0x1E5;
        filtered = Enumerable.Range(0, 48).Select(i => (ushort)(1000 + i)).ToArray();
        baseline = Enumerable.Range(0, 48).Select(i => (ushort)(0x200 + i * 4)).ToArray(); // 全部是 4 的倍数
        status = [1, 2, 3, 4]; i2c = 7;
        thresholds = Enumerable.Range(0, 34).Select(i => (byte)(i * 2)).ToArray();

        var payload = new List<byte> { mask };
        payload.AddRange(BitConverter.GetBytes(timestamp));
        if ((mask & ProtocolConstants.FieldMaskTouch) != 0) for (var i = 0; i < 5; i++) payload.Add((byte)(touch >> (8 * i)));
        if ((mask & ProtocolConstants.FieldMaskFiltered) != 0) foreach (var f in filtered) payload.AddRange(BitConverter.GetBytes(f));
        if ((mask & ProtocolConstants.FieldMaskBaseline) != 0) foreach (var b in baseline) payload.Add((byte)(b >> 2)); // 原始 8 位寄存器值
        if ((mask & ProtocolConstants.FieldMaskStatus) != 0) { payload.AddRange(status); payload.AddRange(BitConverter.GetBytes(i2c)); }
        if ((mask & ProtocolConstants.FieldMaskExtended) != 0)
        {
            for (var i = 0; i < 5; i++) payload.Add((byte)(hw >> (8 * i)));
            payload.AddRange(thresholds);
            for (var i = 0; i < 5; i++) payload.Add((byte)(active >> (8 * i)));
        }
        return payload.ToArray();
    }

    [Fact]
    public void FullMaskRoundTripsAllFields()
    {
        var payload = BuildPayload(0x1F, out var ts, out var touch, out var filtered, out var baseline, out var status, out var i2c, out var hw, out var thresholds, out var active);
        Assert.Equal(PayloadCodecsMaskedLength(0x1F), payload.Length);
        var frame = DebugFrame.DecodeMasked(payload);
        Assert.Equal(ts, frame.TimestampMs);
        Assert.Equal(touch, frame.TouchBits);
        Assert.Equal(filtered, frame.Filtered);
        Assert.Equal(baseline, frame.Baseline); // 8 位寄存器值 << 2 无损还原
        Assert.Equal(status, frame.ChipStatus);
        Assert.Equal(i2c, frame.I2cErrors);
        Assert.Equal(hw, frame.HardwareTouchBits);
        Assert.Equal(thresholds, frame.CompensationThresholds);
        Assert.Equal(active, frame.CompensationActiveBits);
    }

    [Fact]
    public void TouchOnlyPayloadDecodes()
    {
        var payload = BuildPayload(ProtocolConstants.FieldMaskTouch, out var ts, out var touch, out _, out _, out _, out _, out _, out _, out _);
        Assert.Equal(10, payload.Length); // mask 1 + ts 4 + touch 5
        var frame = DebugFrame.DecodeMasked(payload);
        Assert.Equal(ts, frame.TimestampMs);
        Assert.Equal(touch, frame.TouchBits);
        Assert.All(frame.Filtered, f => Assert.Equal(0, f));
        Assert.All(frame.Baseline, b => Assert.Equal(0, b));
        Assert.Null(frame.HardwareTouchBits);
    }

    [Fact]
    public void FilteredOnlyPayloadDecodes()
    {
        var payload = BuildPayload(ProtocolConstants.FieldMaskFiltered, out var ts, out _, out var filtered, out _, out _, out _, out _, out _, out _);
        Assert.Equal(101, payload.Length);
        var frame = DebugFrame.DecodeMasked(payload);
        Assert.Equal(ts, frame.TimestampMs);
        Assert.Equal(filtered, frame.Filtered);
        Assert.Equal(0UL, frame.TouchBits);
        Assert.All(frame.Baseline, b => Assert.Equal(0, b));
    }

    private static int PayloadCodecsMaskedLength(byte mask) => 5
        + ((mask & ProtocolConstants.FieldMaskTouch) != 0 ? 5 : 0)
        + ((mask & ProtocolConstants.FieldMaskFiltered) != 0 ? 96 : 0)
        + ((mask & ProtocolConstants.FieldMaskBaseline) != 0 ? 48 : 0)
        + ((mask & ProtocolConstants.FieldMaskStatus) != 0 ? 6 : 0)
        + ((mask & ProtocolConstants.FieldMaskExtended) != 0 ? 44 : 0);
}
