using System.Buffers.Binary;
using System.Security.Cryptography;
using TURZX.WinUsbDisplay;
using Xunit;

namespace TURZX.WinUsbDisplay.Tests;

/// <summary>验证 Win207 请求包的头部、参数和负载格式。</summary>
public class EncodePacketTests
{
    /// <summary>用于验证请求编码的固定测试密钥。</summary>
    private static readonly byte[] Key = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77];

    /// <summary>解密生成的头部以检查协议字段。</summary>
    private static byte[] DecryptHeader(byte[] packet)
    {
        using var des = DES.Create();
        des.Key = Key;
        byte[] header = new byte[500];
        des.DecryptCbc(packet.AsSpan(0, 504), Key, header, PaddingMode.PKCS7);
        return header;
    }

    /// <summary>验证封包总长度等于协议头加媒体负载。</summary>
    [Fact]
    public void PacketLengthIsHeaderPlusPayload()
    {
        byte[] payload = new byte[1024];
        byte[] packet = DisplaySession.EncodePacket(Key, 1, 102, payload);
        Assert.Equal(512 + payload.Length, packet.Length);
    }

    /// <summary>验证协议固定的包尾标记。</summary>
    [Fact]
    public void TailMarkerIsFixed()
    {
        byte[] packet = DisplaySession.EncodePacket(Key, 1, 10, []);
        Assert.Equal(0xa1, packet[510]);
        Assert.Equal(0x1a, packet[511]);
    }

    /// <summary>验证头部的命令字节、魔数和时间戳。</summary>
    [Fact]
    public void HeaderCarriesCommandMagicAndTimestamp()
    {
        byte[] packet = DisplaySession.EncodePacket(Key, 0x01020304, 13, [], value: 2);
        byte[] header = DecryptHeader(packet);
        Assert.Equal(13, header[0]);
        Assert.Equal(0x1a, header[2]);
        Assert.Equal(0x6d, header[3]);
        Assert.Equal(0x01020304u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)));
        Assert.Equal(2, header[8]);
    }

    /// <summary>验证用大端字节顺序存放负载长度。</summary>
    [Fact]
    public void PayloadLengthIsBigEndian()
    {
        byte[] payload = new byte[0x010203];
        byte[] packet = DisplaySession.EncodePacket(Key, 1, 102, payload);
        byte[] header = DecryptHeader(packet);
        Assert.Equal(0x010203, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8)));
        Assert.Equal(0, header[12]);
    }

    /// <summary>验证最后一个传输块带有结束标志。</summary>
    [Fact]
    public void FinalBlockFlagIsSet()
    {
        byte[] payload = new byte[8];
        byte[] packet = DisplaySession.EncodePacket(Key, 1, 121, payload, finalBlock: true);
        byte[] header = DecryptHeader(packet);
        Assert.Equal(1, header[12]);
    }

    /// <summary>验证媒体负载以原始字节附加在头部之后。</summary>
    [Fact]
    public void PayloadIsAppendedUnencrypted()
    {
        byte[] payload = [1, 2, 3, 4, 5];
        byte[] packet = DisplaySession.EncodePacket(Key, 1, 102, payload);
        Assert.Equal(payload, packet.AsSpan(512).ToArray());
    }

    /// <summary>验证命令参数从协议头第八字节起覆盖。</summary>
    [Fact]
    public void ArgumentsOverrideHeaderOffsetEight()
    {
        byte[] time = [0x07, 0xea, 11, 11, 10, 20, 30, 1];
        byte[] packet = DisplaySession.EncodePacket(Key, 1, 51, [], arguments: time);
        byte[] header = DecryptHeader(packet);
        Assert.Equal(time, header.AsSpan(8, 8).ToArray());
    }

    /// <summary>验证关闭负载长度字段时头部布局。</summary>
    [Fact]
    public void NoPayloadLengthWhenDisabled()
    {
        byte[] packet = DisplaySession.EncodePacket(Key, 1, 51, [], arguments: [1, 2, 3, 4], writePayloadLength: false);
        byte[] header = DecryptHeader(packet);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, header.AsSpan(8, 4).ToArray());
    }

    /// <summary>验证填满参数区时附加负载保持完整。</summary>
    [Fact]
    public void FullArgumentHeaderPreservesPayload()
    {
        byte[] arguments = Enumerable.Range(0, 492).Select(index => unchecked((byte)index)).ToArray();
        byte[] payload = [0x12, 0x34];

        byte[] packet = DisplaySession.EncodePacket(Key, 1, 40, payload,
            arguments: arguments, writePayloadLength: false);

        Assert.Equal(arguments, DecryptHeader(packet).AsSpan(8, 492).ToArray());
        Assert.Equal(payload, packet.AsSpan(512).ToArray());
    }
}