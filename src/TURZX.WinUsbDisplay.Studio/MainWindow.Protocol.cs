using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using static TURZX.WinUsbDisplay.Studio.StudioLanguage;

namespace TURZX.WinUsbDisplay.Studio;

// 协议文档、命令调试和封包参数。
public partial class MainWindow
{
    /// <summary>按文档名称缓存已读入的 Markdown 内容。</summary>
    private readonly Dictionary<string, string> documentation = new(StringComparer.Ordinal);
    /// <summary>当前渲染在文档面板中的 Markdown 源文本。</summary>
    private string renderedMarkdown = "";
    /// <summary>最近选中的协议请求预设。</summary>
    private DeviceCommand? lastPacketPreset;
    /// <summary>协议工具当前对应的设备接口路径。</summary>
    private string? packetDevicePath;
    /// <summary>用于重新渲染协议工具状态的最近一次错误。</summary>
    private Exception? lastPacketError;
    /// <summary>最近一次协议响应及其命令和通道。</summary>
    private (byte Command, bool Raw, byte[] Data)? lastPacketReply;

    /// <summary>获取当前语言对应的文档文件名。</summary>
    private static string DocumentationFileName(string name) =>
        name + StudioLanguage.DocumentationSuffix + ".md";

    /// <summary>读取并缓存所选文档的 Markdown 源内容。</summary>
    private string LoadDocumentation(string name)
    {
        string fileName = DocumentationFileName(name);
        if (!documentation.TryGetValue(fileName, out string? markdown))
        {
            markdown = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "docs", fileName));
            documentation.Add(fileName, markdown);
        }
        return markdown;
    }

    /// <summary>切换到新选中的文档。</summary>
    private void OnDocumentationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProtocolText is not null && ReferenceEquals(e.Source, DocumentationChoice))
            ShowSelectedDocumentation();
    }

    /// <summary>渲染选中的内置文档。</summary>
    private void ShowSelectedDocumentation()
    {
        try
        {
            string name = DocumentationChoice.SelectedIndex == 1 ? "developer-guide" : "win207-protocol";
            renderedMarkdown = LoadDocumentation(name);
            ProtocolText.Document = MarkdownPreview.Render(renderedMarkdown, OpenDocumentationLink);
            ProtocolText.Selection.Select(ProtocolText.Document.ContentStart, ProtocolText.Document.ContentStart);
        }
        catch (IOException ex)
        {
            Status.Text = T("CouldNotOpenTheDocumentation", DescribeUiError(ex));
        }
    }

    /// <summary>打开文档中的受支持链接。</summary>
    private void OpenDocumentationLink(string destination)
    {
        if (destination == DocumentationFileName("developer-guide"))
            DocumentationChoice.SelectedIndex = 1;
        else if (destination == DocumentationFileName("win207-protocol"))
            DocumentationChoice.SelectedIndex = 0;
        else if (Uri.TryCreate(destination, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    /// <summary>在协议搜索框中按键时处理查找操作。</summary>
    private void OnProtocolQueryKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        FindProtocol();
        e.Handled = true;
    }

    /// <summary>响应查找按钮并搜索协议文档。</summary>
    private void OnFindProtocol(object sender, RoutedEventArgs e) => FindProtocol();

    /// <summary>在协议参考中查找输入的文本。</summary>
    private void FindProtocol()
    {
        string query = ProtocolQuery.Text.Trim();
        if (query.Length == 0)
        {
            Status.Text = T("EnterProtocolSearch");
            ProtocolQuery.Focus();
            return;
        }

        if (!MarkdownPreview.FindNext(ProtocolText, query))
        {
            Status.Text = T("NoMatchFor", query);
            return;
        }

        Status.Text = T("Found", query);
    }

    /// <summary>将协议文档内容复制到剪贴板。</summary>
    private void OnCopyProtocol(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ProtocolText.Selection.IsEmpty ? renderedMarkdown : ProtocolText.Selection.Text);
            Status.Text = T("DocumentationCopied");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Status.Text = T("ClipboardBusy");
        }
    }

    /// <summary>将设备命令预设填入协议请求编辑器。</summary>
    private void LoadPacketPreset(DeviceCommand command)
    {
        if (command == lastPacketPreset)
            return;

        lastPacketPreset = command;
        ResetPacketFile();
        lastPacketError = null;
        lastPacketReply = null;
        PacketId.Text = command.Raw ? $"0x{command.Code:X2}" : command.Code.ToString();
        PacketChannel.SelectedIndex = command.Raw ? 1 : 0;
        PacketArguments.Clear();
        PacketPayload.Clear();
        SetPacketReplyStatus("WaitingForARequest");
    }


    /// <summary>解析十进制或十六进制形式的命令编号。</summary>
    private static bool TryParsePacketId(string text, out byte command)
    {
        string id = text.Trim();
        bool hex = id.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        return byte.TryParse(hex ? id.AsSpan(2) : id.AsSpan(),
            hex ? System.Globalization.NumberStyles.AllowHexSpecifier : System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out command);
    }

    /// <summary>协议请求内容变化时同步字段和响应状态。</summary>
    private void OnPacketRequestChanged(object sender, RoutedEventArgs e)
    {
        if (lastPacketReply is not null || lastPacketError is not null)
        {
            lastPacketReply = null;
            lastPacketError = null;
            SetPacketReplyStatus("WaitingForARequest");
        }

        if (ReferenceEquals(sender, PacketId) || ReferenceEquals(sender, PacketChannel))
        {
            ResetPacketFile();
            UpdatePacketFields();
        }
    }

    /// <summary>根据所选通道及命令启用适用的请求字段。</summary>
    private void UpdatePacketFields()
    {
        if (PacketArgumentsHint is null)
            return;
        bool raw = PacketChannel.SelectedIndex == 1;
        bool validCommand = TryParsePacketId(PacketId.Text, out byte command);
        var (arguments, payload) = validCommand ? DeviceCommands.GetRequestFields(command, raw) : (true, !raw);
        PacketArgumentsPlaceholder.Text = T(validCommand ? DeviceCommands.GetArgumentsPlaceholderKey(command, raw)
            : "HexArgumentsHint");
        PacketPayloadPlaceholder.Text = T(validCommand ? DeviceCommands.GetPayloadPlaceholderKey(command, raw)
            : "HexPayloadHint");
        PacketArgumentsRow.Visibility = arguments ? Visibility.Visible : Visibility.Collapsed;
        PacketPayloadRow.Visibility = payload ? Visibility.Visible : Visibility.Collapsed;
        PacketArgumentsHint.Visibility = arguments ? Visibility.Visible : Visibility.Collapsed;
        UpdatePacketFileFields(validCommand && !raw ? command : null);
    }

    /// <summary>解析协议编辑器输入并校验请求长度。</summary>
    private bool TryReadPacketRequest(out byte command, out bool raw, out byte[] arguments, out byte[] payload)
    {
        raw = PacketChannel.SelectedIndex == 1;
        arguments = [];
        payload = [];
        if (!TryParsePacketId(PacketId.Text, out command))
        {
            Status.Text = T("InvalidCommandId");
            return false;
        }

        var fields = DeviceCommands.GetRequestFields(command, raw);
        try
        {
            if (!raw && packetFilePath is not null && SupportsPacketFile(command))
            {
                arguments = BuildPacketFileArguments(command);
                return true;
            }
            arguments = fields.Arguments ? ParseHex(PacketArguments.Text) : [];
            payload = fields.Payload ? ParseHex(PacketPayload.Text) : [];
        }
        catch (FormatException)
        {
            Status.Text = T("IncompleteHexByte");
            return false;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Status.Text = DescribeUiError(error);
            return false;
        }

        if (raw && (arguments.Length > 63 || payload.Length != 0))
        {
            Status.Text = T("InvalidRawPacketLength");
            return false;
        }
        if (!raw && arguments.Length > 492)
        {
            Status.Text = T("EncryptedArgumentsTooLong");
            return false;
        }

        return true;
    }

    /// <summary>向所选设备发送协议请求并保存应答。</summary>
    private async void OnSendPacket(object sender, RoutedEventArgs e)
    {
        if (busy || SelectedDevice is null
            || !TryReadPacketRequest(out byte command, out bool raw, out byte[] arguments, out byte[] payload))
            return;

        string? filePath = !raw && SupportsPacketFile(command) ? packetFilePath : null;
        lastPacketError = null;
        lastPacketReply = null;
        SetPacketReplyStatus("WaitingForTheDeviceResponse");
        DescribePacketRequest(command, raw);
        bool parameterless = filePath is null && arguments.Length == 0 && payload.Length == 0;
        byte[] response = [];
        CancellationToken cancellation = deviceControlShutdown.Token;
        await RunDeviceControlAsync(T("WaitingForTheDeviceResponse"), session =>
        {
            response = raw ? session.ExchangeRawCommand(command, arguments)
                : parameterless && command is 10 or 50 or 100
                    ? session.ReadVerifiedQueryResponse(command, cancellation)
                    : session.ExchangeCommand(command, arguments, payload);
            return T("DeviceResponded");
        }, message =>
        {
            lastPacketReply = (command, raw, response);
            RenderPacketReply();
            return message;
        }, onFailure: error =>
        {
            lastPacketError = error;
            RenderPacketReply();
        }, prepare: filePath is null ? null : async token =>
        {
            payload = await ReadPacketFileAsync(command, filePath, arguments, token).ConfigureAwait(false);
        });
    }

    /// <summary>显示请求命令及其通道的可读描述。</summary>
    private void DescribePacketRequest(byte command, bool raw)
    {
        var requestedCommand = DeviceCommands.All.FirstOrDefault(item => item.Code == command && item.Raw == raw);
        CommandResult.Text = requestedCommand is not null
            ? DescribeCommand(requestedCommand)
            : T("NoDescriptionIsAvailableForThisCommand");
    }

    /// <summary>设置协议应答面板的状态提示。</summary>
    private void SetPacketReplyStatus(string statusKey)
    {
        PacketResponse.SetResourceReference(TextBox.TextProperty, ResourceKey(statusKey));
        PacketParsedResponse.SetResourceReference(TextBox.TextProperty, ResourceKey(statusKey));
    }

    /// <summary>根据缓存的应答或错误刷新协议面板。</summary>
    private void RenderPacketReply()
    {
        if (lastPacketError is { } error)
        {
            PacketParsedResponse.Text = DescribeUiError(error);
            PacketResponse.Text = error is DeviceResponseException { Response.IsEmpty: false } response
                ? FormatPacketResponse(response.Response.ToArray())
                : PacketParsedResponse.Text;
            PacketResponse.ScrollToHome();
            PacketParsedResponse.ScrollToHome();
        }
        else if (lastPacketReply is { } reply)
        {
            PacketResponse.Text = FormatPacketResponse(reply.Data);
            PacketParsedResponse.Text = DeviceCommands.DescribeQueryReply(reply.Command, reply.Raw, reply.Data)
                ?? T("NoDecodedResponse");
            PacketResponse.ScrollToHome();
            PacketParsedResponse.ScrollToHome();
        }
    }

    /// <summary>生成并显示当前协议请求的调用示例。</summary>
    private void OnShowPacketExample(object sender, RoutedEventArgs e)
    {
        if (!TryReadPacketRequest(out byte command, out bool raw, out byte[] arguments, out byte[] payload))
            return;

        string id = raw ? $"0x{command:X2}" : command.ToString(System.Globalization.CultureInfo.InvariantCulture);
        bool usesFile = !raw && packetFilePath is not null && SupportsPacketFile(command);
        string request;
        string prepareFile = "";
        if (usesFile)
        {
            byte[] signature = PacketImageSignature(command).ToArray();
            string validateImage = signature.Length == 0 ? "" :
                $"if (!payload.AsSpan().StartsWith<byte>({HexArgument(signature)}))\n" +
                "    throw new ArgumentException(\"The local file does not match the command format.\");\n";
            prepareFile = $$"""
                using var file = System.IO.File.OpenRead(args[1]);
                if (file.Length < 1 || file.Length > {{PacketFileLimit(command)}})
                    throw new ArgumentException("The file is empty or too large for this command.");
                byte[] payload = new byte[(int)file.Length];
                file.ReadExactly(payload);
                {{validateImage}}byte[] arguments = {{HexArgument(arguments)}};
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(arguments.AsSpan({{(command == 40 ? 4 : 0)}}), payload.Length);

                """;
            request = $"session.ExchangeCommand({id}, arguments, payload)";
        }
        else
        {
            request = raw
                ? $"session.ExchangeRawCommand({id}, {HexArgument(arguments)})"
                : arguments.Length == 0 && payload.Length == 0 && command is 10 or 50 or 100
                    ? $"session.ReadVerifiedQueryResponse({id})"
                    : $"session.ExchangeCommand({id}, {HexArgument(arguments)}, {HexArgument(payload)})";
        }

        PacketExampleCode.Text = $$"""
            using System;
            using System.Security.Cryptography;
            using TURZX.WinUsbDisplay;

            var devices = DisplayDevice.Discover(DisplaySettings.InterfaceGuid);
            if (args.Length != {{(usesFile ? 2 : 1)}} || !int.TryParse(args[0], out int index) || index < 0 || index >= devices.Count)
            {
                for (int i = 0; i < devices.Count; i++)
                    Console.Error.WriteLine($"{i}: {devices[i].Model.Name}");
                Console.Error.WriteLine("Usage: <device-index>{{(usesFile ? " <local-file>" : "")}}");
                return;
            }

            {{prepareFile}}var device = devices[index];
            byte[] key = DisplaySettings.ReadCommandKey();
            try
            {
                using var session = device.Open(key);
                byte[] response = {{request}};
                Console.WriteLine(Convert.ToHexString(response));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
            """;
        PacketExampleStatus.Text = "";
        PacketExampleOverlay.Visibility = Visibility.Visible;
        PacketExampleClose.Focus();

        // 用十六进制文本构造示例中的参数表达式。
        static string HexArgument(byte[] data) => data.Length == 0
            ? "Array.Empty<byte>()" : $"Convert.FromHexString(\"{Convert.ToHexString(data)}\")";
    }

    /// <summary>将协议调用示例复制到剪贴板。</summary>
    private void OnCopyPacketExample(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(PacketExampleCode.Text);
            PacketExampleStatus.Text = T("CodeCopied");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            PacketExampleStatus.Text = T("ClipboardBusy");
        }
    }

    /// <summary>关闭协议调用示例弹层。</summary>
    private void OnClosePacketExample(object sender, RoutedEventArgs e) => ClosePacketExample();

    /// <summary>处理协议示例弹层的键盘关闭操作。</summary>
    private void OnPacketExampleKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ClosePacketExample();
            e.Handled = true;
        }
    }

    /// <summary>清除并隐藏协议调用示例。</summary>
    private void ClosePacketExample()
    {
        PacketExampleOverlay.Visibility = Visibility.Collapsed;
        CommandList.Focus();
    }

    /// <summary>将十六进制输入转换为字节数组。</summary>
    private static byte[] ParseHex(string text) =>
        Convert.FromHexString(string.Concat(text.Where(character => !char.IsWhiteSpace(character))));

    /// <summary>将设备原始应答转换为可检查的文本。</summary>
    private static string FormatPacketResponse(byte[] data)
    {
        var output = new System.Text.StringBuilder(T("ReceivedBytes", data.Length));
        for (int offset = 0; offset < data.Length; offset += 16)
        {
            int count = Math.Min(16, data.Length - offset);
            var line = data.AsSpan(offset, count);
            string hex = BitConverter.ToString(data, offset, count).Replace('-', ' ');
            output.AppendLine().Append($"{offset:X4}  {hex,-47}  ");
            foreach (byte value in line)
                output.Append(value is >= 32 and <= 126 ? (char)value : '.');
        }
        return output.ToString();
    }

    /// <summary>选中不同快捷命令时刷新其用途和可用性。</summary>
    private void OnCommandChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CommandList.SelectedItem is DeviceCommand selected)
            LoadPacketPreset(selected);

        CommandResult.Text = CommandList.SelectedItem is DeviceCommand command
            ? DescribeCommand(command)
            : T("SelectCommandHint");
        UpdateCommandControls();
    }

    /// <summary>生成当前语言下的设备命令说明。</summary>
    private string DescribeCommand(DeviceCommand command)
    {
        string label = $"{command.Purpose} · {command.Route}"
            + (command.Query || DeviceCommands.CanExecute(command) ? "" : T("UseThePacketConsole"));
        try
        {
            string id = command.Raw ? $"0x{command.Code:X2}" : command.Code.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string prefix = $"| {id} | {command.ProtocolName} | ";
            string markdown = LoadDocumentation("win207-protocol");
            string? row = markdown.Split('\n').FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal));
            if (row is not null)
                return label + "\n" + row[prefix.Length..].Split('|', 2)[0].Trim().Replace("`", "");
        }
        catch (IOException)
        {
            return label + "\n" + T("DocumentationUnavailable");
        }
        return label;
    }

    /// <summary>按所选设备和运行状态更新快捷命令控件。</summary>
    private void UpdateCommandControls()
    {
        var command = CommandList.SelectedItem as DeviceCommand;
        CommandList.IsEnabled = PacketInputs.IsEnabled = !busy;
        SendPacket.IsEnabled = !busy && SelectedDevice is not null;
        bool presetAvailable = DeviceCommands.CanExecute(command);
        RunCommand.Visibility = presetAvailable ? Visibility.Visible : Visibility.Collapsed;
        RunCommand.IsEnabled = !busy && SelectedDevice is not null && presetAvailable;
    }

    /// <summary>执行当前选中的设备快捷命令。</summary>
    private async void OnRunCommand(object sender, RoutedEventArgs e)
    {
        if (CommandList.SelectedItem is not DeviceCommand command || !DeviceCommands.CanExecute(command))
            return;

        await RunDeviceControlAsync(T("CommunicatingWithTheDevice"), session => DeviceCommands.Run(session, command), result =>
        {
            CommandResult.Text = DescribeCommand(command) + "\n" + T("Response", result);
            return T("DeviceResponded");
        });
    }
}
