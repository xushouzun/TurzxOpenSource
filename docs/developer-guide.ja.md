# TURZX WinUsbDisplay DLL 開発ガイド

`TURZX.WinUsbDisplay.dll` は、Windows 上で Win207 デバイスを制御する `net10.0` 向けライブラリです。Windows 標準の SetupAPI と WinUSB を使用し、NuGet パッケージへの依存はありません。デバイスには WinUSB ドライバーをインストールしてください。

## 1. ビルドと参照

対応するリリースから `TURZX-WinUsbDisplay-SDK.zip` をダウンロードします。展開先のルートで `dotnet run --project examples/SdkDiscover` を実行すると、デバイスを列挙できます。`lib` にはデバイス DLL、任意のメディア DLL、およびそれぞれの XML API ドキュメントが含まれます。ソースからビルドすることもできます。

デバイス制御 DLL のみをビルドします。

```powershell
dotnet build src/TURZX.WinUsbDisplay/TURZX.WinUsbDisplay.csproj -c Release
```

生成された `TURZX.WinUsbDisplay.dll` を利用側プロジェクトの `lib` ディレクトリにコピーします。IntelliSense 用に、同じディレクトリの `TURZX.WinUsbDisplay.xml` もコピーできます。デバイスの列挙、ファームウェア情報の取得、ディスプレイの設定には、この DLL の参照だけで十分です。デスクトップアプリ、CLI、FFmpeg、ブラウザーは任意です。

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="TURZX.WinUsbDisplay">
      <HintPath>lib/TURZX.WinUsbDisplay.dll</HintPath>
    </Reference>
  </ItemGroup>
</Project>
```

ソースから開発する場合は、`src/TURZX.WinUsbDisplay/TURZX.WinUsbDisplay.csproj` への `ProjectReference` を使用します。Studio と CLI の構成は、デバイス SDK と任意のメディアライブラリを参照する例です。後者は `TURZX.WinUsbDisplay.Media.dll` としてビルドされ、公開型は `TURZX.WinUsbDisplay.Media` 名前空間にあります。メディアライブラリを使う場合は両方の DLL を参照してください。

## 2. 初回接続

デバイスの 8 バイトのコマンドパケットキーをファームウェアの保守担当者から入手します。環境変数を使うアプリでは、起動前に対応する 16 桁の 16 進文字列を `WINUSB_DISPLAY_COMMAND_KEY` に設定してください。独自の設定から 8 バイトを読み取り、`device.Open(key)` に直接渡すこともできます。

```csharp
using System.Security.Cryptography;
using TURZX.WinUsbDisplay;

var devices = DisplayDevice.Discover(DisplaySettings.InterfaceGuid);
for (int index = 0; index < devices.Count; index++)
{
    var model = devices[index].Model;
    Console.WriteLine($"{index}: {model.Name} {model.Width}x{model.Height}");
}
if (args.Length != 1 || !int.TryParse(args[0], out int target) || target < 0 || target >= devices.Count)
    return;

byte[] key = DisplaySettings.ReadCommandKey();
try
{
    using DisplaySession screen = devices[target].Open(key);
    Console.WriteLine(screen.ReadFirmwareVersion());
    screen.SetBrightness(60);
    screen.SetRotation(0);
    // 950,000 バイト以下の完全な 8 ビット RGBA PNG を使用します。
    screen.ShowImage(File.ReadAllBytes("artwork.png"));
}
finally
{
    CryptographicOperations.ZeroMemory(key);
}
```

アプリでは現在の接続先を `DisplayDevice.Path` で保持します。リストのインデックスが有効なのは、その列挙結果に限られます。再列挙後にパスが見つからない場合は、ユーザーにデバイスの再選択を求めます。ドライバーが独自のインターフェイス GUID を登録する場合は、`Discover` に直接渡すか `WINUSB_DISPLAY_INTERFACE_GUID` を設定します。

## 3. API と入力

| API | 用途と入力 |
| --- | --- |
| `DisplayDevice.Discover(guid)` | 既知のモデルを列挙し、インターフェイスパスとネイティブの幅・高さを返します。デバイスが見つからない場合は空のリストを返します |
| `device.Open(key)` | 8 バイトのキーでセッションを開きます。セッションはキーのコピーを保持し、破棄時にゼロで消去します |
| `ReadFirmwareVersion()` | 検証済みのファームウェアバージョン応答を読み取ります |
| `SetBrightness(percent)` | 範囲は 0～100。12.3 インチモデルでは明るさの上限が自動的に適用されます |
| `SetRotation(quarterTurns)` | 0～3 は 0°、90°、180°、270° に対応します。再生中の動画を停止してから変更してください |
| `ShowImage(bytes)` | 950,000 バイト以下の完全な JPEG または 8 ビット RGBA PNG を渡します。送信前にストリーミング動画と時計レイヤーを停止します |
| `ShowOverlay(png)` | 950,000 バイト以下の完全な 8 ビット RGBA PNG を前景として渡します。H.264 背景は継続し、透過部分はファームウェアが合成します |
| `PlayVideo(stream, fps, loop, cancellation, onStarted)` | 読み取り可能な Annex B H.264 ストリームを渡します。ループ再生にはシークが必要です。ブロッキング呼び出しなのでバックグラウンドスレッドで実行してください。任意のコールバックは先頭ブロックへの応答後に呼ばれます。読み取り中にキャンセルした場合は、元の操作がまだ完了していないことがあるため、新しいストリームで再試行してください。 |
| `SetFrameRate(fps)` | フレームレートだけを毎秒 1～60 フレームに設定します。`PlayVideo` で動画を転送する場合は、同メソッドがフレームレートを設定します |
| `ReadDeviceDirectory(directory, cancellation)` | ファイル名と `IsComplete` を返します。応答が不完全でも一部の項目が返る場合があります。フォルダーの作成に失敗すると、`Failure` が `DirectoryCreationFailed` の `DeviceResponseException` をスローします |
| `PlayDeviceFile(path)` / `DeleteDeviceFile(path)` | デバイス内のファイルを再生または削除します。パスは `/` から始まる ASCII で、最大 484 バイトです。削除応答には共通の成功コードがないため、一覧を再取得して削除を確認してください |
| `ReadStorageCapacity()` | SD カードと内部フラッシュの容量を KiB 単位で返します |
| `ReadSupplyVoltageMillivolts()` | 対応デバイスの電源電圧を読み取ります。元のプロジェクトでは指定の 12.3 インチモデルで mV 単位の値を表示します |
| `StopPlayback()` | ホストのストリーミング、デバイス内の H.264 再生、JPEG ループを停止します。先に実行中の転送をキャンセルし、終了を待ってください |
| `StopVideo()` | ストリーミング動画の停止コマンドを送信します。連続送信中は、先にそのタスクをキャンセルして終了を待ってください |
| `StopLocalVideo()`, `StopImageLoop()` | デバイスに保存された動画または画像のループ再生を停止します |
| `ShowDefaultClock(enabled)` | ホストのローカル時刻でデバイスの時計を合わせて表示するか、時計表示を停止します |
| `SwitchToDesktop()` | 対応ファームウェアをデバイスのデスクトップモードに切り替えます |
| `ExchangeCommand(command, arguments, payload)` | 暗号化チャネルで汎用の送受信を行います。引数は平文ヘッダーのオフセット 8 から最大 492 バイト、ペイロードは USB のオフセット 512 からです。長さとフラグは呼び出し側でプロトコルに従って設定します。実際の応答バイトを返します |
| `ExchangeRawCommand(command, arguments)` | RAW チャネルで汎用の送受信を行います。コマンド番号はオフセット 0、引数はオフセット 1 から最大 63 バイトです。実際の応答バイトを返します |
| `ReadDiagnostic(command, raw)` | 生の応答を返します。暗号化チャネルは 10、17、50、60、100、112、122、201、RAW チャネルは 0x60、0x62 に対応します |

デバイスライブラリはエンコード済みの画像や動画を直接受け取ります。MP4 などのコンテナーは先に Annex B H.264 に変換し、HTML や Windows のウィンドウは PNG にレンダリングします。バイト列を自前で用意するか、`TURZX.WinUsbDisplay.Media` の `VideoConverter`、`HtmlCapture`、`LiveScene`、`FrameInput` を参照してください。

## 4. セッションとエラー処理

- 1 台のディスプレイを同時に使うプロセスは 1 つにします。接続後は `using` でセッションを管理し、使用後に解放します。
- USB 呼び出しは同期処理です。GUI ではデバイスの読み書きをワーカースレッドで実行してください。同じセッションでライブ前景を `PlayVideo` と交互に送れます。ライブラリは要求と応答の組ごとに直列化します。
- ループ再生を停止するには、`CancellationToken` をキャンセルし、転送処理の終了を待ってからセッションを破棄するかコンテンツを切り替えます。開始済みの USB 要求は、応答またはエンドポイントのタイムアウトまで待機します。
- デバイスを開くときは、`DisplayOpenException.Failure` で共有の競合（`InUse`）とアクセス拒否（`AccessDenied`）を区別できます。動画転送のキャンセル後にデバイスの停止コマンドが失敗すると、`PlaybackStopException` が発生します。
- WinUSB エラーは、操作名と Windows エラーコードを含む `Win32Exception` として報告されます。書き込みの不足、空または不正な応答は `IOException` になることがあります。デバイスが切断された場合は、古いセッションを破棄して再列挙し、送信済みブロックを無条件に再送しないでください。
- 診断応答を解析する前に実際の長さを確認してください。コマンドに対応していないモデルやファームウェアは、タイムアウトするか異なる形式を返す場合があります。

コマンド番号、引数のオフセット、バイト順、パケット例については [Win207 デバイスコマンドリファレンス](win207-protocol.ja.md) を参照してください。
