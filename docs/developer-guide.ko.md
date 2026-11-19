# TURZX WinUsbDisplay DLL 개발 가이드

`TURZX.WinUsbDisplay.dll`은 Windows에서 Win207 장치를 제어하는 `net10.0` 대상 라이브러리입니다. Windows에 포함된 SetupAPI와 WinUSB를 사용하며 NuGet 패키지 종속성은 없습니다. 장치에 WinUSB 드라이버를 설치해야 합니다.

## 1. 빌드 및 참조

해당 릴리스에서 `TURZX-WinUsbDisplay-SDK.zip`을 내려받습니다. 압축을 푼 루트에서 `dotnet run --project examples/SdkDiscover`를 실행하면 장치를 열거할 수 있습니다. `lib` 디렉터리에는 장치 DLL과 선택적 미디어 DLL 및 각 DLL의 XML API 문서가 들어 있습니다. 소스에서 빌드할 수도 있습니다.

장치 제어 DLL만 빌드합니다.

```powershell
dotnet build src/TURZX.WinUsbDisplay/TURZX.WinUsbDisplay.csproj -c Release
```

생성된 `TURZX.WinUsbDisplay.dll`을 사용하는 프로젝트의 `lib` 디렉터리에 복사합니다. IntelliSense를 사용하려면 같은 디렉터리의 `TURZX.WinUsbDisplay.xml`도 복사할 수 있습니다. 장치 열거, 펌웨어 정보 읽기 및 디스플레이 설정에는 이 DLL만 참조하면 됩니다. 데스크톱 앱, CLI, FFmpeg 및 브라우저는 선택 사항입니다.

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

소스에서 개발하는 경우 `src/TURZX.WinUsbDisplay/TURZX.WinUsbDisplay.csproj`에 대한 `ProjectReference`를 사용합니다. Studio와 CLI에서 장치 SDK와 선택적 미디어 라이브러리를 참조하는 구성을 확인할 수 있습니다. 미디어 라이브러리는 `TURZX.WinUsbDisplay.Media.dll`로 빌드되며 공개 형식은 `TURZX.WinUsbDisplay.Media` 네임스페이스에 있습니다. 미디어 라이브러리를 사용할 때는 두 DLL을 모두 참조하세요.

## 2. 첫 연결

펌웨어 유지 관리자에게서 장치의 8바이트 명령 패킷 키를 받습니다. 환경 변수를 사용하는 앱은 시작 전에 해당 16자리 16진수 문자열을 `WINUSB_DISPLAY_COMMAND_KEY`에 설정하세요. 자체 설정에서 8바이트를 읽어 `device.Open(key)`에 직접 전달할 수도 있습니다.

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
    // 950,000바이트 이하의 완전한 8비트 RGBA PNG를 사용합니다.
    screen.ShowImage(File.ReadAllBytes("artwork.png"));
}
finally
{
    CryptographicOperations.ZeroMemory(key);
}
```

앱에서는 현재 연결 대상을 `DisplayDevice.Path`로 유지합니다. 목록 인덱스는 해당 열거 결과에서만 유효합니다. 다시 열거한 후 경로가 없으면 사용자에게 장치를 다시 선택하도록 안내하세요. 드라이버가 사용자 지정 인터페이스 GUID를 등록하는 경우 `Discover`에 직접 전달하거나 `WINUSB_DISPLAY_INTERFACE_GUID`를 설정합니다.

## 3. API 및 입력

| API | 용도 및 입력 |
| --- | --- |
| `DisplayDevice.Discover(guid)` | 알려진 모델을 열거하여 인터페이스 경로와 기본 너비 및 높이를 반환합니다. 장치가 없으면 빈 목록을 반환합니다 |
| `device.Open(key)` | 8바이트 키로 세션을 엽니다. 세션은 키 사본을 보관하고 해제할 때 0으로 지웁니다 |
| `ReadFirmwareVersion()` | 검증된 펌웨어 버전 응답을 읽습니다 |
| `SetBrightness(percent)` | 범위는 0~100입니다. 12.3인치 모델에는 밝기 상한이 자동으로 적용됩니다 |
| `SetRotation(quarterTurns)` | 0~3은 0°, 90°, 180°, 270°에 해당합니다. 현재 동영상을 중지한 후 변경하세요 |
| `ShowImage(bytes)` | 950,000바이트 이하의 완전한 JPEG 또는 8비트 RGBA PNG를 전달합니다. 전송 전에 스트리밍 동영상과 시계 레이어를 중지합니다 |
| `ShowOverlay(png)` | 950,000바이트 이하의 완전한 8비트 RGBA PNG를 전경으로 전달합니다. H.264 배경은 계속 재생되며 펌웨어가 투명 영역을 합성합니다 |
| `PlayVideo(stream, fps, loop, cancellation, onStarted)` | 읽기 가능한 Annex B H.264 스트림을 전달합니다. 반복 재생에는 위치 이동이 필요합니다. 차단 호출이므로 백그라운드 스레드에서 실행하세요. 선택적 콜백은 첫 블록의 응답을 받은 후 호출됩니다. 읽는 중에 취소했다면 원래 작업이 아직 끝나지 않았을 수 있으므로 새 스트림으로 다시 시도하세요. |
| `SetFrameRate(fps)` | 초당 1~60프레임으로 프레임 속도를 설정합니다. `PlayVideo`로 동영상을 전송하면 해당 메서드가 프레임 속도를 설정합니다 |
| `ReadDeviceDirectory(directory, cancellation)` | 파일 이름과 `IsComplete`를 반환합니다. 응답이 불완전해도 일부 항목이 반환될 수 있습니다. 디렉터리 생성에 실패하면 `Failure`가 `DirectoryCreationFailed`인 `DeviceResponseException`을 던집니다 |
| `PlayDeviceFile(path)` / `DeleteDeviceFile(path)` | 장치에 저장된 파일을 재생하거나 삭제합니다. `/`로 시작하는 최대 484바이트의 ASCII 경로를 사용하세요. 삭제 응답에는 공통 성공 코드가 없으므로 목록을 다시 조회해 삭제를 확인하세요 |
| `ReadStorageCapacity()` | SD 카드와 내부 플래시의 용량을 KiB 단위로 반환합니다 |
| `ReadSupplyVoltageMillivolts()` | 호환되는 장치의 전원 전압을 밀리볼트 단위로 읽습니다 |
| `StopPlayback()` | 호스트 스트리밍, 장치 내 H.264 재생 및 JPEG 반복 재생을 중지합니다. 먼저 진행 중인 전송을 취소하고 종료를 기다리세요 |
| `StopVideo()` | 스트리밍 동영상 중지 명령을 보냅니다. 연속 전송이 진행 중이면 먼저 해당 작업을 취소하고 종료를 기다리세요 |
| `StopLocalVideo()`, `StopImageLoop()` | 장치에 저장된 동영상 또는 이미지의 반복 재생을 중지합니다 |
| `ShowDefaultClock(enabled)` | 호스트의 현지 시간으로 장치 시계를 맞춰 표시하거나 시계 표시를 중지합니다 |
| `SwitchToDesktop()` | 다음 부팅에 Windows 데스크톱 모드를 설정합니다. 적용하려면 호출 측에서 별도로 다시 시작(11)합니다 |
| `ExchangeCommand(command, arguments, payload)` | 암호화 채널에서 범용 송수신을 수행합니다. 매개 변수는 평문 헤더 오프셋 8부터 최대 492바이트이며 페이로드는 USB 오프셋 512부터 시작합니다. 호출자가 프로토콜에 맞게 길이와 플래그를 채웁니다. 실제 응답 바이트를 반환합니다 |
| `ExchangeRawCommand(command, arguments)` | 원시 채널에서 범용 송수신을 수행합니다. 명령 번호는 오프셋 0에 있으며 매개 변수는 오프셋 1부터 최대 63바이트입니다. 실제 응답 바이트를 반환합니다 |
| `ReadDiagnostic(command, raw)` | 원시 응답을 반환합니다. 암호화 채널은 10, 17, 50, 60, 100, 112, 122, 201을 지원하고 원시 채널은 0x60, 0x62를 지원합니다 |

`DesktopModeDevice.Discover()`로 데스크톱 모드 장치를 열거합니다. `Serial`로 선택하고 `SwitchToMonitorAsync()`로 HID 보고서를 한 번 보낸 다음 `DisplayDevice.Discover(DisplaySettings.InterfaceGuid)`로 동일한 일련번호의 WinUSB 인터페이스가 나타났는지 확인합니다. Studio에서 조작하는 방법은 프로토콜 참조의 ‘Windows 데스크톱 모드와 USB 보조 화면 모드’를 참고하세요.

장치 라이브러리는 인코딩된 이미지나 동영상을 직접 받습니다. MP4 같은 컨테이너는 먼저 Annex B H.264로 변환하고 HTML과 Windows 창은 PNG 또는 JPEG로 렌더링해야 합니다. 바이트를 직접 생성하거나 `TURZX.WinUsbDisplay.Media`의 `VideoConverter`, `HtmlCapture`, `LiveScene`, `FrameInput`을 참고하세요.

### HTML 캡처 출력 회전

`HtmlCapture.OpenAsync(source, model, cancellation, fastCapture, pixelScale, outputRotation)`은 브라우저 합성 단계에서 캡처 출력을 회전합니다. 페이지 스크립트에 표시되는 논리 뷰포트는 계속 `model`로 정의합니다. 예를 들어 논리 크기가 1280 × 800이고 `pixelScale=1`, `outputRotation=3`이면 출력은 800 × 1280입니다.

| `outputRotation` | 출력 방향 |
| --- | --- |
| `0` | 기존 동작을 유지합니다. 기존 오버로드는 모두 이 값을 사용합니다 |
| `1` | 시계 반대 방향 90° |
| `2` | 180° |
| `3` | 시계 방향 90° |

홀수 값은 출력의 너비와 높이를 바꿉니다. `pixelScale`은 계속 픽셀 밀도를 지정합니다. 0이 아닌 회전은 로컬 HTML만 지원합니다. 페이지는 논리 크기를 유지하는 `iframe` 안에서 로드되며 CSS, 스크립트, 이미지의 상대 URL은 원본 HTML 주소를 기준으로 해석됩니다. 페이지는 `iframe` 안에서 실행될 수 있어야 합니다. 원격 HTTP(S) URL에 0이 아닌 출력 회전을 지정하면 `NotSupportedException`이 발생합니다. `outputRotation=0`인 일반 원격 캡처는 영향을 받지 않습니다. 이 매개 변수는 장치 회전 명령을 보내지 않습니다.

회전은 PNG와 JPEG 캡처에 모두 적용됩니다. `ReadFrameAsync`와 `ReadPngScreencastAsync`는 PNG를, `ReadJpegFrameAsync`와 `ReadJpegScreencastAsync`는 JPEG를 반환합니다. 각 쌍은 각각 필요할 때 요청하는 스크린샷과 연속 프레임 전송을 제공합니다.

## 4. 세션 및 오류 처리

- 디스플레이 한 대는 한 번에 하나의 프로세스에서만 사용하세요. 연결한 후 `using`으로 세션을 관리하고 사용이 끝나면 해제합니다.
- USB 호출은 동기식입니다. GUI에서는 장치 읽기와 쓰기를 작업자 스레드에서 실행하세요. 같은 세션에서 실시간 전경을 `PlayVideo`와 번갈아 전송할 수 있습니다. 라이브러리는 각 요청과 응답 쌍을 직렬화합니다.
- 반복 재생을 중지하려면 `CancellationToken`을 취소하고 전송이 반환될 때까지 기다린 후 세션을 해제하거나 콘텐츠를 전환하세요. 취소 시 이미 시작된 USB 요청은 응답이 오거나 엔드포인트 제한 시간이 지날 때까지 대기합니다.
- 장치를 열 때 `DisplayOpenException.Failure`로 공유 충돌(`InUse`)과 접근 거부(`AccessDenied`)를 구분할 수 있습니다. 동영상 전송을 취소한 후 장치 중지 명령이 실패하면 `PlaybackStopException`이 발생합니다.
- WinUSB 오류는 작업과 Windows 오류 코드를 포함한 `Win32Exception`으로 보고됩니다. 일부만 기록되거나 응답이 비어 있거나 유효하지 않으면 `IOException`이 발생할 수 있습니다. 장치 연결이 끊기면 기존 세션을 해제하고 다시 열거하세요. 이미 전달한 블록을 무조건 다시 보내지 마세요.
- 진단 응답을 분석하기 전에 실제 길이를 확인하세요. 명령을 지원하지 않는 모델이나 펌웨어는 시간이 초과되거나 다른 형식으로 응답할 수 있습니다.

명령 번호, 매개 변수 오프셋, 바이트 순서 및 패킷 예제는 [Win207 장치 명령 참조](win207-protocol.ko.md)를 참고하세요.
