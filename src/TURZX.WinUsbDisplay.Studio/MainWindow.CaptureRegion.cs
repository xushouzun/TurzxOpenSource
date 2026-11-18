using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using static TURZX.WinUsbDisplay.Studio.StudioLanguage;

namespace TURZX.WinUsbDisplay.Studio;

// 管理窗口和网页捕捉源的裁剪区域。
public partial class MainWindow
{
    // 裁剪区域按源画面的宽高归一化，并跟随所选捕捉源。
    /// <summary>保存裁剪区域及其所属捕捉源。</summary>
    private (object Source, Rect Bounds)? captureRegion;
    /// <summary>用于框选或重绘裁剪预览的完整源帧。</summary>
    private byte[]? regionPreviewFrame;
    /// <summary>指示预览画布是否处于裁剪框选状态。</summary>
    private bool selectingCaptureRegion;
    /// <summary>记录裁剪框选手势的起点。</summary>
    private Point? regionDragOrigin;

    /// <summary>返回用于区分各捕捉源的标识。</summary>
    private object? CaptureRegionSource => LiveSource.SelectedIndex switch
    {
        0 when LiveSourceText(0).Length > 0 => LiveSourceText(0),
        1 when preferredCaptureWindowTitle is { Length: > 0 } title => (WindowMode: 1, Title: title),
        WebSourceMode when IsWebUrl(LiveSourceText(WebSourceMode)) => LiveSourceText(WebSourceMode),
        _ => null
    };

    /// <summary>仅在当前来源与保存的来源一致时返回裁剪区域。</summary>
    private Rect? CurrentCaptureRegion => captureRegion is { } region && Equals(region.Source, CaptureRegionSource)
        ? region.Bounds : null;

    /// <summary>按当前来源和预览状态更新裁剪操作控件。</summary>
    private void UpdateCaptureRegionControls()
    {
        if (CaptureRegionRow is null)
            return;

        CaptureRegionRow.Visibility = LiveSource.SelectedIndex is 0 or 1 or WebSourceMode
            ? Visibility.Visible : Visibility.Collapsed;
        SelectCaptureRegion.IsEnabled = !busy && regionPreviewFrame is not null && CaptureRegionSource is not null;
        SelectCaptureRegion.SetResourceReference(ContentControl.ContentProperty,
            ResourceKey(selectingCaptureRegion ? "CancelSelection" : "SelectRegion"));
        ResetCaptureRegion.Visibility = CurrentCaptureRegion.HasValue ? Visibility.Visible : Visibility.Collapsed;
        ResetCaptureRegion.IsEnabled = !selectingCaptureRegion;
    }

    /// <summary>开始框选，或取消已在进行的框选。</summary>
    private void OnSelectCaptureRegion(object sender, RoutedEventArgs e)
    {
        if (selectingCaptureRegion)
        {
            FinishCaptureRegionSelection();
            return;
        }
        if (busy || regionPreviewFrame is not { } frame || CaptureRegionSource is null)
            return;

        EndPreviewDrag();
        selectingCaptureRegion = true;
        HomeCanvas.SetRegionSelection(true);
        // 框选时展示完整源画面，便于重新选择已有裁剪区域之外的内容。
        HomeCanvas.ShowFrame(frame, overlay: true);
        Status.Text = T("CaptureRegionInstructions");
        UpdateMediaControls();
    }

    /// <summary>清除当前裁剪区域并刷新预览。</summary>
    private void OnResetCaptureRegion(object sender, RoutedEventArgs e)
    {
        captureRegion = null;
        RefreshCaptureRegionPreview();
        UpdateMediaControls();
    }

    /// <summary>应用用户确认的裁剪区域并退出框选。</summary>
    private void FinishCaptureRegionSelection(Rect? bounds = null)
    {
        if (!selectingCaptureRegion)
            return;

        if (bounds is { } region && CaptureRegionSource is { } source)
            captureRegion = (source, region);

        selectingCaptureRegion = false;
        regionDragOrigin = null;
        EndPreviewDrag();
        HomeCanvas.SetRegionSelection(false);
        RefreshCaptureRegionPreview();
        UpdateMediaControls();
        Status.Text = T(bounds.HasValue ? "CaptureRegionSelected" : "RegionSelectionCanceled");
    }

    /// <summary>用当前裁剪区域重绘最近的完整源帧。</summary>
    private void RefreshCaptureRegionPreview()
    {
        if (regionPreviewFrame is { } frame)
            HomeCanvas.ShowFrame(frame, overlay: true, crop: CurrentCaptureRegion);
    }
}
