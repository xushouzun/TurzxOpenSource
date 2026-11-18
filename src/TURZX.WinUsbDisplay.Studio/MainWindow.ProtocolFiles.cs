using System.Buffers.Binary;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using static TURZX.WinUsbDisplay.Studio.StudioLanguage;

namespace TURZX.WinUsbDisplay.Studio;

public partial class MainWindow
{
    private string? packetFilePath;

    private static bool SupportsPacketFile(byte command) => command is 40 or 101 or 102;

    private static int PacketFileLimit(byte command) =>
        command == 40 ? 100 * 1024 - 1 : DisplayFrameProcessor.MaxEncodedFrameBytes;

    private static ReadOnlySpan<byte> PacketImageSignature(byte command) => command switch
    {
        101 => DisplaySession.JpegSignature,
        102 => DisplaySession.PngSignature,
        _ => []
    };

    private void ResetPacketFile()
    {
        packetFilePath = null;
        PacketDevicePath?.Clear();
    }

    private void UpdatePacketFileFields(byte? command)
    {
        bool supported = command is byte value && SupportsPacketFile(value);
        bool attached = supported && packetFilePath is not null;
        PacketChooseFile.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        PacketChooseVideo.Visibility = command == 121 ? Visibility.Visible : Visibility.Collapsed;
        PacketFileRow.Visibility = attached ? Visibility.Visible : Visibility.Collapsed;
        PacketDevicePathRow.Visibility = attached && command == 40 ? Visibility.Visible : Visibility.Collapsed;
        PacketPayloadHexEditor.Visibility = attached ? Visibility.Collapsed : Visibility.Visible;
        PacketPayloadLabel.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty,
            ResourceKey(attached ? "PacketLocalFile" : "PayloadHexOptional"));
        if (attached)
        {
            PacketArgumentsRow.Visibility = Visibility.Collapsed;
            PacketArgumentsHint.Visibility = Visibility.Collapsed;
        }
    }

    private void OnChoosePacketFile(object sender, RoutedEventArgs e)
    {
        if (busy || PacketChannel.SelectedIndex == 1 || !TryParsePacketId(PacketId.Text, out byte command)
            || !SupportsPacketFile(command))
            return;

        var picker = new OpenFileDialog
        {
            Filter = command switch
            {
                101 => "JPEG|*.jpg;*.jpeg",
                102 => "PNG|*.png",
                _ => T("PacketLocalFile") + "|*.*"
            }
        };
        if (picker.ShowDialog(this) != true)
            return;

        packetFilePath = picker.FileName;
        PacketFileName.Text = Path.GetFileName(packetFilePath);
        PacketFileName.ToolTip = packetFilePath;
        OnPacketRequestChanged(sender, e);
        UpdatePacketFields();
        if (command == 40)
            PacketDevicePath.Focus();
    }

    private void OnClearPacketFile(object sender, RoutedEventArgs e)
    {
        ResetPacketFile();
        OnPacketRequestChanged(sender, e);
        UpdatePacketFields();
    }

    private void OnChoosePacketVideo(object sender, RoutedEventArgs e)
    {
        if (busy)
            return;
        var picker = new OpenFileDialog { Filter = "H.264 (Annex B)|*.h264;*.264" };
        if (picker.ShowDialog(this) != true)
            return;

        MediaNav.IsChecked = true;
        BackgroundPath.Text = picker.FileName;
        Status.Text = T("PacketVideoSelected");
    }

    private byte[] BuildPacketFileArguments(byte command)
    {
        byte[] arguments = new byte[4];
        if (command == 40)
        {
            string devicePath = PacketDevicePath.Text.Trim();
            try
            {
                arguments = DisplaySession.EncodeDevicePath(devicePath);
                if (devicePath.EndsWith('/'))
                    throw new ArgumentException("Expected a file path.");
            }
            catch (ArgumentException error)
            {
                throw UiError(new InvalidDataException("Enter an absolute ASCII device file path.", error),
                    "PacketInvalidDevicePath");
            }
        }

        return arguments;
    }

    private static async Task<byte[]> ReadPacketFileAsync(byte command, string path, byte[] arguments,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        long length = file.Length;
        if (length < 1 || length > PacketFileLimit(command))
            throw UiError(new InvalidDataException("The local file is empty or exceeds the packet size limit."),
                command == 40 ? "PacketShortFileSize" : "PacketImageSize");
        byte[] payload = new byte[(int)length];
        await file.ReadExactlyAsync(payload, cancellation).ConfigureAwait(false);

        if (!payload.AsSpan().StartsWith(PacketImageSignature(command)))
            throw UiError(new InvalidDataException("The selected file does not match the command format."),
                command == 101 ? "PacketInvalidJpeg" : "PacketInvalidPng");

        BinaryPrimitives.WriteInt32BigEndian(arguments.AsSpan(command == 40 ? 4 : 0), payload.Length);
        return payload;
    }
}
