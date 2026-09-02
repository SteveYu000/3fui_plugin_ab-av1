Imports System.Drawing
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports LakeUI

''' <summary>
''' ab-av1 sample-encode 的独立任务页。任务只评估指定 CRF 的样本质量与完整编码预测，
''' 不会创建临时 3FUI 预设，也不会加入宿主的正式编码队列。
''' </summary>
Friend NotInheritable Class SampleEncodePanel
    Inherits UserControl

    Private Shared ReadOnly ColorBackground As Color = Color.FromArgb(24, 24, 24)
    Private Shared ReadOnly ColorControl As Color = Color.FromArgb(40, 220, 220, 220)
    Private Shared ReadOnly ColorControlHover As Color = Color.FromArgb(60, 220, 220, 220)
    Private Shared ReadOnly ColorControlPressed As Color = Color.FromArgb(80, 220, 220, 220)
    Private Shared ReadOnly ColorText As Color = Color.FromArgb(220, 220, 220)
    Private Shared ReadOnly ColorMuted As Color = Color.FromArgb(120, 255, 255, 255)
    Private Shared ReadOnly ColorAccent As Color = Color.FromArgb(71, 156, 255)
    Private Shared ReadOnly ColorSuccess As Color = Color.FromArgb(63, 205, 135)
    Private Shared ReadOnly ColorWarning As Color = Color.FromArgb(232, 177, 74)
    Private Shared ReadOnly ColorDanger As Color = Color.FromArgb(235, 93, 93)
    Private Const ProgressRenderIntervalMilliseconds As Integer = 80

    Private ReadOnly _presetPath As ModernTextBox
    Private ReadOnly _scoreMetric As ModernComboBox
    Private ReadOnly _crf As ModernTextBox
    Private ReadOnly _samples As ModernTextBox
    Private ReadOnly _sampleDuration As ModernTextBox
    Private ReadOnly _vmafModel As ModernComboBox
    Private ReadOnly _fileList As UltraDetailListView
    Private ReadOnly _presetSummary As HtmlColorLabel
    Private ReadOnly _environmentStatus As HtmlColorLabel
    Private ReadOnly _vmafModelStatus As HtmlColorLabel
    Private ReadOnly _status As HtmlColorLabel
    Private ReadOnly _progressRing As ProgressRing
    Private ReadOnly _startButton As ModernButton
    Private ReadOnly _addFilesButton As ModernButton
    Private ReadOnly _stopButton As ModernButton
    Private ReadOnly _pauseResumeButton As ModernButton
    Private ReadOnly _removeButton As ModernButton
    Private ReadOnly _resetButton As ModernButton
    Private ReadOnly _refreshModelsButton As ModernButton
    Private ReadOnly _browseModelButton As ModernButton
    Private ReadOnly _copyCommandLineButton As ModernButton
    Private ReadOnly _taskContextMenu As ModernContextMenu
    Private ReadOnly _progressRenderTimer As System.Windows.Forms.Timer
    Private ReadOnly _detailFont As Font
    Private _vmafModelRow As GpuGridPanel
    Private _modelRowStyle As RowStyle

    Private ReadOnly _items As New List(Of SampleQueueFileItem)()
    Private ReadOnly _lifetimeCancellation As New CancellationTokenSource()
    Private _activeItem As SampleQueueFileItem
    Private _contextMenuTarget As SampleQueueFileItem
    Private _schedulerTask As Task
    Private _running As Boolean
    Private _scanningModels As Boolean
    Private _initialModelScanStarted As Boolean
    Private _pendingProgressItem As SampleQueueFileItem
    Private _pendingProgressStatus As String
    Private _pendingProgressScore As String

    Public Sub New()
        SuspendLayout()
        'Keep this native host control out of the LakeUI 5 child-surface paint path.
        DoubleBuffered = False
        BackColor = Color.Transparent
        ForeColor = ColorText
        Font = New Font("Microsoft YaHei UI", 10.0F)
        _detailFont = New Font("Microsoft YaHei UI", 8.5F)
        Dock = DockStyle.Fill
        AllowDrop = True
        Padding = Padding.Empty

        _presetPath = CreateTextBox("选择 FFmpegFreeUI v6 JSON 预设")
        _scoreMetric = CreateMetricComboBox()
        _crf = CreateTextBox("要评估的 CRF")
        _crf.Text = "30"
        _samples = CreateTextBox("留空使用 ab-av1 自动采样")
        _sampleDuration = CreateTextBox("20s")
        _sampleDuration.Text = "20s"
        _vmafModel = CreateComboBox("留空使用 ab-av1 自动模型；也可直接输入模型名称")

        _fileList = CreateFileList()
        _fileList.AllowDrop = True
        _presetSummary = CreateLabel("尚未读取预设", ColorMuted, 9.0F)
        _environmentStatus = CreateLabel(String.Empty, ColorMuted, 9.0F)
        _vmafModelStatus = CreateLabel("等待扫描当前 ffmpeg", ColorMuted, 8.5F)
        _status = CreateLabel("就绪", ColorMuted, 9.0F)

        _progressRing = New ProgressRing With {
            .Size = New Size(30, 30),
            .RingColor = ColorAccent,
            .AutoStart = False,
            .Visible = False,
            .Margin = New Padding(2, 8, 10, 8)
        }

        _addFilesButton = CreateButton("添加媒体", AddressOf AddFiles)
        _stopButton = CreateButton("停止", AddressOf StopTasks, danger:=True)
        _pauseResumeButton = CreateButton("暂停 / 恢复", AddressOf PauseOrResumeTasks)
        _removeButton = CreateButton("移除", AddressOf RemoveTasks)
        _resetButton = CreateButton("重置状态", AddressOf ResetTasks)
        _refreshModelsButton = CreateButton("扫描模型", AddressOf RefreshModels)
        _browseModelButton = CreateButton("本地 JSON", AddressOf BrowseVmafModel)
        _copyCommandLineButton = CreateButton("复制命令行", AddressOf CopyCommandLineTemplate)
        _startButton = CreateButton("开始样本队列", AddressOf StartQueue, accent:=True)
        _taskContextMenu = CreateTaskContextMenu()
        _progressRenderTimer = New System.Windows.Forms.Timer With {
            .Interval = ProgressRenderIntervalMilliseconds
        }
        AddHandler _progressRenderTimer.Tick, AddressOf ProgressRenderTimerTick

        Dim root = BuildLayout()
        Controls.Add(root)

        AddHandler _presetPath.LostFocus, AddressOf PresetPathLostFocus
        AddHandler _scoreMetric.SelectedIndexChanged, AddressOf ScoreMetricChanged
        AddHandler _fileList.SelectedIndexChanged, AddressOf QueueSelectionChanged
        AddHandler _fileList.KeyDown, AddressOf QueueKeyDown
        AddHandler _fileList.MouseUp, AddressOf QueueMouseUp
        AddHandler Me.Load, AddressOf SampleEncodePanelLoad
        AddHandler Me.DragEnter, AddressOf FilesDragEnter
        AddHandler Me.DragOver, AddressOf FilesDragOver
        AddHandler Me.DragDrop, AddressOf FilesDragDrop
        AddHandler _fileList.DragEnter, AddressOf FilesDragEnter
        AddHandler _fileList.DragOver, AddressOf FilesDragOver
        AddHandler _fileList.DragDrop, AddressOf FilesDragDrop

        _presetPath.Text = PluginEnvironment.FindDefaultPreset()
        RefreshEnvironmentStatus()
        RefreshPresetSummary()
        UpdateScoreMetricUi()
        RefreshActionButtons()
        ResumeLayout(False)
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then
            _lifetimeCancellation.Cancel()
            _progressRenderTimer.Stop()
            RemoveHandler _progressRenderTimer.Tick, AddressOf ProgressRenderTimerTick
            _progressRenderTimer.Dispose()
            For Each item In _items.ToArray()
                item.Cancellation?.Cancel()
            Next
            _taskContextMenu.Dispose()
            _detailFont.Dispose()
        End If
        MyBase.Dispose(disposing)
    End Sub

    Private Async Sub SampleEncodePanelLoad(sender As Object, e As EventArgs)
        If _initialModelScanStarted Then Return
        _initialModelScanStarted = True
        If GetSelectedMetric() = QualityMetric.Vmaf Then Await RefreshVmafModelsAsync()
    End Sub

    Private Function BuildLayout() As Control
        Dim root As New GpuGridPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 1,
            .RowCount = 4,
            .Margin = Padding.Empty,
            .Padding = Padding.Empty,
            .BackColor = Color.Transparent
        }
        root.SuspendLayout()
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 140))
        root.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        root.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 68))
        root.Controls.Add(BuildPathSection(), 0, 0)
        root.Controls.Add(BuildSettingsSection(), 0, 1)
        root.Controls.Add(BuildFileSection(), 0, 2)
        root.Controls.Add(BuildFooter(), 0, 3)
        root.ResumeLayout(False)
        Return root
    End Function

    Private Function BuildPathSection() As Control
        Dim layout As New GpuGridPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 5,
            .RowCount = 3,
            .Padding = Padding.Empty,
            .BackColor = Color.Transparent
        }
        layout.SuspendLayout()
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 150))
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 12))
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 12))
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 180))
        layout.RowStyles.Add(New RowStyle(SizeType.Absolute, 44))
        layout.RowStyles.Add(New RowStyle(SizeType.Absolute, 52))
        layout.RowStyles.Add(New RowStyle(SizeType.Absolute, 40))

        Dim topBar As New GpuGridPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 2,
            .RowCount = 1,
            .Margin = Padding.Empty,
            .Padding = Padding.Empty,
            .BackColor = Color.Transparent
        }
        topBar.SuspendLayout()
        topBar.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 72))
        topBar.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 28))
        topBar.Controls.Add(
            CreateSectionHeading("AB-AV1 样本编码", "用指定 CRF 评估样本质量与完整编码预测，不加入正式编码队列"),
            0,
            0)
        _environmentStatus.Dock = DockStyle.Fill
        _environmentStatus.TextAlign = HtmlColorLabel.TextAlignEnum.MiddleRight
        topBar.Controls.Add(_environmentStatus, 1, 0)
        topBar.ResumeLayout(False)
        layout.Controls.Add(topBar, 0, 0)
        layout.SetColumnSpan(topBar, 5)

        Dim browsePresetButton = CreateButton("选择预设", AddressOf BrowsePreset)
        browsePresetButton.Dock = DockStyle.Fill
        browsePresetButton.Margin = New Padding(0, 6, 12, 6)
        layout.Controls.Add(browsePresetButton, 0, 1)
        layout.Controls.Add(_presetPath, 2, 1)
        _copyCommandLineButton.Dock = DockStyle.Fill
        _copyCommandLineButton.Margin = New Padding(0, 6, 0, 6)
        layout.Controls.Add(_copyCommandLineButton, 4, 1)

        Dim summaryCaption = CreateLabel("当前预设", ColorMuted, 9.5F)
        summaryCaption.Dock = DockStyle.Fill
        summaryCaption.TextAlign = HtmlColorLabel.TextAlignEnum.MiddleLeft
        layout.Controls.Add(summaryCaption, 0, 2)
        _presetSummary.Dock = DockStyle.Fill
        _presetSummary.TextAlign = HtmlColorLabel.TextAlignEnum.MiddleLeft
        layout.Controls.Add(_presetSummary, 2, 2)
        layout.SetColumnSpan(_presetSummary, 3)
        layout.ResumeLayout(False)
        Return layout
    End Function

    Private Function BuildSettingsSection() As Control
        Dim layout As New GpuGridPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 4,
            .RowCount = 3,
            .Padding = New Padding(0, 10, 0, 10),
            .BackColor = Color.Transparent
        }
        layout.SuspendLayout()
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 18.0F))
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 18.0F))
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 36.0F))
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 28.0F))
        layout.RowStyles.Add(New RowStyle(SizeType.Absolute, 44))
        layout.RowStyles.Add(New RowStyle(SizeType.Absolute, 82))
        layout.RowStyles.Add(New RowStyle(SizeType.Absolute, 74))
        _modelRowStyle = layout.RowStyles(2)

        Dim heading = CreateSectionHeading("样本参数", "指定 CRF、评分指标和采样方式；每个文件独立生成预测结果")
        layout.Controls.Add(heading, 0, 0)
        layout.SetColumnSpan(heading, 4)
        layout.Controls.Add(CreateSettingsField("评分指标", _scoreMetric), 0, 1)
        layout.Controls.Add(CreateSettingsField("CRF", _crf), 1, 1)
        layout.Controls.Add(CreateSettingsField("采样数量", _samples), 2, 1)
        layout.Controls.Add(CreateSettingsField("单段时长", _sampleDuration), 3, 1)

        _vmafModelRow = New GpuGridPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 5,
            .RowCount = 1,
            .Margin = Padding.Empty,
            .Padding = New Padding(0, 10, 0, 4),
            .BackColor = Color.Transparent
        }
        _vmafModelRow.SuspendLayout()
        _vmafModelRow.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 150))
        _vmafModelRow.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        _vmafModelRow.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 140))
        _vmafModelRow.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 140))
        _vmafModelRow.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 300))
        Dim modelCaption = CreateFieldLabel("VMAF 模型")
        modelCaption.AutoSize = False
        modelCaption.TextAlign = HtmlColorLabel.TextAlignEnum.MiddleLeft
        _vmafModelRow.Controls.Add(modelCaption, 0, 0)
        _vmafModel.Margin = New Padding(0, 5, 12, 5)
        _vmafModelRow.Controls.Add(_vmafModel, 1, 0)
        _refreshModelsButton.Dock = DockStyle.Fill
        _browseModelButton.Dock = DockStyle.Fill
        _refreshModelsButton.Margin = New Padding(0, 5, 12, 5)
        _browseModelButton.Margin = New Padding(0, 5, 12, 5)
        _vmafModelRow.Controls.Add(_refreshModelsButton, 2, 0)
        _vmafModelRow.Controls.Add(_browseModelButton, 3, 0)
        _vmafModelStatus.Dock = DockStyle.Fill
        _vmafModelStatus.TextAlign = HtmlColorLabel.TextAlignEnum.MiddleLeft
        _vmafModelRow.Controls.Add(_vmafModelStatus, 4, 0)
        _vmafModelRow.ResumeLayout(False)
        layout.Controls.Add(_vmafModelRow, 0, 2)
        layout.SetColumnSpan(_vmafModelRow, 4)
        layout.ResumeLayout(False)
        Return layout
    End Function

    Private Function BuildFileSection() As Control
        Dim layout As New GpuGridPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 1,
            .RowCount = 3,
            .Padding = New Padding(0, 0, 0, 10),
            .BackColor = Color.Transparent
        }
        layout.SuspendLayout()
        layout.RowStyles.Add(New RowStyle(SizeType.Absolute, 46))
        layout.RowStyles.Add(New RowStyle(SizeType.Absolute, 60))
        layout.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        layout.Controls.Add(
            CreateSectionHeading("样本任务", "运行中仍可拖入或添加文件；新任务会进入当前等待队列"),
            0,
            0)

        Dim toolbar As New GpuFlowPanel With {
            .Dock = DockStyle.Fill,
            .FlowDirection = ModernPanel.FlowDirectionEnum.LeftToRight,
            .WrapContents = False,
            .BackColor = Color.Transparent,
            .Margin = Padding.Empty
        }
        toolbar.SuspendLayout()
        For Each button In {_addFilesButton, _stopButton, _pauseResumeButton, _removeButton, _resetButton}
            button.Size = New Size(132, 42)
            button.Margin = New Padding(0, 9, 12, 9)
            toolbar.Controls.Add(button)
        Next
        Dim hint = CreateLabel("选择任务后操作；未选择时【停止/暂停】作用于当前任务", ColorMuted, 9.0F)
        hint.AutoSize = True
        hint.Margin = New Padding(10, 19, 0, 0)
        toolbar.Controls.Add(hint)
        toolbar.ResumeLayout(False)
        layout.Controls.Add(toolbar, 0, 1)
        layout.Controls.Add(_fileList, 0, 2)
        layout.ResumeLayout(False)
        Return layout
    End Function

    Private Function BuildFooter() As Control
        Dim layout As New GpuGridPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 3,
            .RowCount = 1,
            .BackColor = Color.Transparent,
            .Margin = Padding.Empty
        }
        layout.SuspendLayout()
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 44))
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 250))
        _status.Dock = DockStyle.Fill
        _status.TextAlign = HtmlColorLabel.TextAlignEnum.MiddleLeft
        _startButton.Dock = DockStyle.Fill
        layout.Controls.Add(_progressRing, 0, 0)
        layout.Controls.Add(_status, 1, 0)
        layout.Controls.Add(_startButton, 2, 0)
        layout.ResumeLayout(False)
        Return layout
    End Function

    Private Shared Function CreateSettingsField(caption As String, editor As Control) As Control
        Dim layout As New GpuGridPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 1,
            .RowCount = 2,
            .BackColor = Color.Transparent,
            .Margin = New Padding(0, 0, 10, 0),
            .Padding = Padding.Empty
        }
        layout.SuspendLayout()
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        layout.RowStyles.Add(New RowStyle(SizeType.Absolute, 28))
        layout.RowStyles.Add(New RowStyle(SizeType.Absolute, 50))
        Dim label = CreateFieldLabel(caption)
        label.TextAlign = HtmlColorLabel.TextAlignEnum.BottomLeft
        layout.Controls.Add(label, 0, 0)
        layout.Controls.Add(editor, 0, 1)
        layout.ResumeLayout(False)
        Return layout
    End Function

    Private Sub RefreshEnvironmentStatus()
        If File.Exists(PluginEnvironment.AbAv1Path) Then
            _environmentStatus.ForeColor = ColorSuccess
            _environmentStatus.Text = "ab-av1.exe 已就绪"
        Else
            _environmentStatus.ForeColor = ColorDanger
            _environmentStatus.Text = "缺少 ab-av1.exe · 请放到插件目录"
        End If
    End Sub

    Private Sub RefreshPresetSummary()
        Try
            Dim profile = PresetProfile.Load(_presetPath.Text)
            _presetSummary.ForeColor = ColorSuccess
            _presetSummary.Text = profile.GetSummary()
        Catch ex As Exception
            _presetSummary.ForeColor = ColorDanger
            _presetSummary.Text = CompactMessage(ex.Message)
        End Try
    End Sub

    Private Sub PresetPathLostFocus(sender As Object, e As EventArgs)
        RefreshPresetSummary()
    End Sub

    Private Sub BrowsePreset(sender As Object, e As EventArgs)
        If _running Then Return
        Using dialog As New OpenFileDialog With {
            .Title = "选择 FFmpegFreeUI v6 预设",
            .Filter = "FFmpegFreeUI JSON 预设 (*.json)|*.json|所有文件 (*.*)|*.*",
            .CheckFileExists = True,
            .Multiselect = False
        }
            Dim current = _presetPath.Text.Trim()
            If File.Exists(current) Then
                dialog.InitialDirectory = Path.GetDirectoryName(current)
                dialog.FileName = Path.GetFileName(current)
            Else
                Dim defaultDirectory = Path.Combine(Application.StartupPath, "Preset_v6", "User")
                If Directory.Exists(defaultDirectory) Then dialog.InitialDirectory = defaultDirectory
            End If
            If dialog.ShowDialog(FindForm()) = DialogResult.OK Then
                _presetPath.Text = dialog.FileName
                RefreshPresetSummary()
            End If
        End Using
    End Sub

    Private Async Sub CopyCommandLineTemplate(sender As Object, e As EventArgs)
        Try
            Dim profile = PresetProfile.Load(_presetPath.Text.Trim())
            Dim settings = ReadSettings()
            Dim commandLine = Await AbAv1Runner.BuildSampleCommandLineTemplateAsync(
                profile,
                settings,
                _lifetimeCancellation.Token)
            CopyCommandLineToClipboard(
                commandLine,
                "已复制当前预设的 sample-encode 命令行模板；<输入文件> 是待替换的路径。")
        Catch ex As OperationCanceledException When _lifetimeCancellation.IsCancellationRequested
        Catch ex As Exception
            ShowCopyCommandLineError(ex)
        End Try
    End Sub

    Private Sub ScoreMetricChanged(sender As Object, e As EventArgs)
        UpdateScoreMetricUi()
    End Sub

    Private Function GetSelectedMetric() As QualityMetric
        Return If(_scoreMetric.SelectedIndex = 1 OrElse
                  String.Equals(_scoreMetric.Text.Trim(), "XPSNR", StringComparison.OrdinalIgnoreCase),
                  QualityMetric.Xpsnr,
                  QualityMetric.Vmaf)
    End Function

    Private Sub UpdateScoreMetricUi()
        If _vmafModelRow Is Nothing OrElse _modelRowStyle Is Nothing Then Return
        Dim showModel = GetSelectedMetric() = QualityMetric.Vmaf
        _modelRowStyle.Height = If(showModel, 74.0F, 0.0F)
        _vmafModelRow.Visible = showModel
        _vmafModel.Enabled = showModel AndAlso Not _running
        _refreshModelsButton.Enabled = showModel AndAlso Not _running AndAlso Not _scanningModels
        _browseModelButton.Enabled = showModel AndAlso Not _running
        PerformLayoutThroughAncestors(_vmafModelRow)
    End Sub

    Private Shared Sub PerformLayoutThroughAncestors(start As Control)
        Dim current = start
        While current IsNot Nothing
            current.PerformLayout()
            current = current.Parent
        End While
    End Sub

    Private Async Sub RefreshModels(sender As Object, e As EventArgs)
        If _running OrElse GetSelectedMetric() <> QualityMetric.Vmaf Then Return
        Await RefreshVmafModelsAsync(forceRefresh:=True)
    End Sub

    Private Async Function RefreshVmafModelsAsync(Optional forceRefresh As Boolean = False) As Task
        If _scanningModels OrElse IsDisposed Then Return
        _scanningModels = True
        _refreshModelsButton.Enabled = False
        _vmafModelStatus.ForeColor = ColorMuted
        _vmafModelStatus.Text = "正在扫描当前 ffmpeg…"
        Dim currentText = _vmafModel.Text
        Try
            Dim result = Await VmafModelScanner.ScanAsync(_lifetimeCancellation.Token, forceRefresh)
            If IsDisposed Then Return
            _vmafModel.Items.Clear()
            For Each model In result.Models
                _vmafModel.Items.Add(model)
            Next
            _vmafModel.SelectedIndex = -1
            _vmafModel.Text = currentText
            If result.Models.Count > 0 Then
                _vmafModelStatus.ForeColor = ColorSuccess
                _vmafModelStatus.Text = $"发现 {result.Models.Count} 个 · {Path.GetFileName(result.FfmpegPath)}"
            Else
                _vmafModelStatus.ForeColor = ColorWarning
                _vmafModelStatus.Text = CompactMessage(result.ErrorMessage)
            End If
        Catch ex As OperationCanceledException
        Finally
            _scanningModels = False
            If Not IsDisposed Then
                _refreshModelsButton.Enabled = Not _running AndAlso GetSelectedMetric() = QualityMetric.Vmaf
            End If
        End Try
    End Function

    Private Sub BrowseVmafModel(sender As Object, e As EventArgs)
        If _running Then Return
        Using dialog As New OpenFileDialog With {
            .Title = "选择本地 VMAF 模型",
            .Filter = "VMAF 模型 JSON (*.json)|*.json|所有文件 (*.*)|*.*",
            .CheckFileExists = True,
            .Multiselect = False
        }
            Dim current = _vmafModel.Text.Trim()
            If File.Exists(current) Then
                dialog.InitialDirectory = Path.GetDirectoryName(current)
                dialog.FileName = Path.GetFileName(current)
            End If
            If dialog.ShowDialog(FindForm()) = DialogResult.OK Then
                _vmafModel.SelectedIndex = -1
                _vmafModel.Text = dialog.FileName
            End If
        End Using
    End Sub

    Private Sub AddFiles(sender As Object, e As EventArgs)
        Using dialog As New OpenFileDialog With {
            .Title = "选择要进行样本编码的媒体文件",
            .Filter = "媒体文件|*.mkv;*.mp4;*.mov;*.m4v;*.webm;*.avi;*.ts;*.m2ts;*.flv;*.wmv|所有文件 (*.*)|*.*",
            .CheckFileExists = True,
            .Multiselect = True
        }
            If dialog.ShowDialog(FindForm()) <> DialogResult.OK Then Return
            Dim added = AddFilePaths(dialog.FileNames)
            UpdateAddFilesStatus(added, dialog.FileNames.Length, dragged:=False)
        End Using
    End Sub

    Private Sub FilesDragEnter(sender As Object, e As DragEventArgs)
        SetFileDropEffect(e)
    End Sub

    Private Sub FilesDragOver(sender As Object, e As DragEventArgs)
        SetFileDropEffect(e)
    End Sub

    Private Sub FilesDragDrop(sender As Object, e As DragEventArgs)
        If e.Data Is Nothing OrElse Not e.Data.GetDataPresent(DataFormats.FileDrop) Then Return
        Dim dropped = TryCast(e.Data.GetData(DataFormats.FileDrop), String())
        If dropped Is Nothing Then Return
        Dim added = AddFilePaths(dropped)
        UpdateAddFilesStatus(added, dropped.Length, dragged:=True)
    End Sub

    Private Shared Sub SetFileDropEffect(e As DragEventArgs)
        If e.Data IsNot Nothing AndAlso
           e.Data.GetDataPresent(DataFormats.FileDrop) AndAlso
           (e.AllowedEffect And DragDropEffects.Copy) = DragDropEffects.Copy Then
            e.Effect = DragDropEffects.Copy
        Else
            e.Effect = DragDropEffects.None
        End If
    End Sub

    Private Function AddFilePaths(filePaths As IEnumerable(Of String)) As Integer
        Dim added = 0
        _fileList.BeginUpdate()
        Try
            For Each candidate In filePaths
                If String.IsNullOrWhiteSpace(candidate) OrElse Not File.Exists(candidate) Then Continue For
                Dim filePath = Path.GetFullPath(candidate)
                If _items.Any(Function(existingItem) String.Equals(existingItem.Path, filePath, StringComparison.OrdinalIgnoreCase)) Then Continue For
                Dim row As New UltraDetailListView.ListItem({
                    New UltraDetailListView.ListSubItem(Path.GetFileName(filePath), Font, ColorText),
                    New UltraDetailListView.ListSubItem("等待", Font, ColorMuted),
                    New UltraDetailListView.ListSubItem("—", Font, ColorMuted),
                    New UltraDetailListView.ListSubItem("—", Font, ColorMuted),
                    New UltraDetailListView.ListSubItem("—", Font, ColorMuted),
                    New UltraDetailListView.ListSubItem("—", Font, ColorMuted)
                })
                Dim item As New SampleQueueFileItem(filePath, row)
                row.Tag = item
                SetBottomLine(item, filePath, ColorMuted)
                _fileList.Items.Add(row)
                _items.Add(item)
                added += 1
            Next
        Finally
            _fileList.EndUpdate()
        End Try
        RefreshActionButtons()
        Return added
    End Function

    Private Sub UpdateAddFilesStatus(added As Integer, supplied As Integer, dragged As Boolean)
        Dim action = If(dragged, "拖入", "选择")
        If added > 0 Then
            Dim skipped = supplied - added
            Dim suffix = If(skipped > 0, $"，忽略 {skipped} 个重复项或非文件项", String.Empty)
            If _running Then
                UpdateStatus($"已{action} {added} 个文件并加入等待队列{suffix}")
            Else
                UpdateStatus($"已{action}并添加 {added} 个文件，列表共 {_items.Count} 个{suffix}")
            End If
        ElseIf supplied > 0 Then
            UpdateStatus($"未添加文件：{action}内容均为重复项或非文件项")
        End If
    End Sub

    Private Async Sub StartQueue(sender As Object, e As EventArgs)
        If _running Then Return
        Try
            RefreshEnvironmentStatus()
            If Not File.Exists(PluginEnvironment.AbAv1Path) Then
                Throw New FileNotFoundException("请将 ab-av1.exe 放到插件 DLL 同一目录。", PluginEnvironment.AbAv1Path)
            End If
            If Not _items.Any(Function(item) item.State = SampleTaskState.Pending) Then
                Throw New InvalidOperationException("没有等待中的任务；可添加文件或先重置任务状态。")
            End If
            Dim profile = PresetProfile.Load(_presetPath.Text.Trim())
            Dim settings = ReadSettings()
            SetRunning(True)
            _schedulerTask = RunQueueAsync(profile, settings)
            Await _schedulerTask
        Catch ex As OperationCanceledException When _lifetimeCancellation.IsCancellationRequested
        Catch ex As Exception
            UpdateStatus("无法开始：" & CompactMessage(ex.Message))
            MessageBox.Show(FindForm(), ex.Message, "AB-AV1 样本编码", MessageBoxButtons.OK, MessageBoxIcon.Warning)
        Finally
            _schedulerTask = Nothing
            If Not IsDisposed Then SetRunning(False)
        End Try
    End Sub

    Private Async Function RunQueueAsync(profile As PresetProfile,
                                         settings As SampleEncodeSettings) As Task
        While Not _lifetimeCancellation.IsCancellationRequested
            Dim item = _items.FirstOrDefault(Function(value) value.State = SampleTaskState.Pending)
            If item Is Nothing Then
                Await Task.Yield()
                item = _items.FirstOrDefault(Function(value) value.State = SampleTaskState.Pending)
                If item Is Nothing Then Exit While
            End If
            Await RunQueueItemAsync(item, profile, settings)
        End While

        If Not IsDisposed Then
            Dim completed = _items.Where(Function(value) value.State = SampleTaskState.Completed).Count()
            Dim failed = _items.Where(Function(value) value.State = SampleTaskState.Failed).Count()
            Dim stopped = _items.Where(Function(value) value.State = SampleTaskState.Stopped).Count()
            UpdateStatus($"样本队列结束：{completed} 个完成，{failed} 个失败，{stopped} 个已停止")
        End If
    End Function

    Private Async Function RunQueueItemAsync(item As SampleQueueFileItem,
                                             profile As PresetProfile,
                                             settings As SampleEncodeSettings) As Task
        _activeItem = item
        item.Runner = New AbAv1Runner()
        item.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token)
        item.Row.SubItems(2).Text = SearchSettings.FormatNumber(settings.Crf)
        SetTaskState(item, SampleTaskState.Running)
        SetBottomLine(item, item.Path, ColorMuted)
        UpdateStatus($"{Path.GetFileName(item.Path)} · 正在启动 ab-av1 sample-encode")
        RefreshRows({item})
        RefreshActionButtons()

        Dim localItem = item
        Dim progress As New Progress(Of SearchProgress)(
            Sub(update)
                If IsDisposed Then Return
                QueueProgressRender(localItem, update)
            End Sub)
        Try
            Dim result = Await item.Runner.SampleEncodeAsync(
                profile,
                item.Path,
                settings,
                progress,
                item.Cancellation.Token)
            item.Result = result
            item.Row.SubItems(2).Text = SearchSettings.FormatNumber(result.Crf)
            item.Row.SubItems(3).Text = FormatMetricScore(result.Metric, result.Score)
            item.Row.SubItems(4).Text = PluginEnvironment.FormatBytes(result.PredictedEncodeSize)
            item.Row.SubItems(5).Text = FormatDuration(result.PredictedEncodeSeconds)
            SetTaskState(item, SampleTaskState.Completed)

            Dim detail = $"预测完整编码：{PluginEnvironment.FormatBytes(result.PredictedEncodeSize)}"
            If result.PredictedEncodeSeconds > 0 Then detail &= $" · {FormatDuration(result.PredictedEncodeSeconds)}"
            If result.PredictedEncodePercent > 0 Then detail &= $" · 输入大小的 {result.PredictedEncodePercent.ToString("0.##", CultureInfo.InvariantCulture)}%"
            SetBottomLine(item, detail, ColorMuted)
        Catch ex As OperationCanceledException
            SetTaskState(item, SampleTaskState.Stopped)
            SetBottomLine(item, "任务已停止；可重置后重新评估", ColorWarning)
        Catch ex As Exception
            If item.Cancellation IsNot Nothing AndAlso item.Cancellation.IsCancellationRequested Then
                SetTaskState(item, SampleTaskState.Stopped)
                SetBottomLine(item, "任务已停止；可重置后重新评估", ColorWarning)
            Else
                item.ErrorMessage = ex.Message
                SetTaskState(item, SampleTaskState.Failed)
                SetBottomLine(item, CompactMessage(ex.Message), ColorDanger)
            End If
        Finally
            ClearPendingProgress(item)
            If item.Runner IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(item.Runner.CurrentCommandLine) Then
                item.CommandLine = item.Runner.CurrentCommandLine
            End If
            item.Cancellation?.Dispose()
            item.Cancellation = Nothing
            item.Runner = Nothing
            If Object.ReferenceEquals(_activeItem, item) Then _activeItem = Nothing
            If Not IsDisposed Then
                RefreshRows({item})
                RefreshActionButtons()
            End If
        End Try
    End Function

    Private Sub StopTasks(sender As Object, e As EventArgs)
        Dim targets = GetOperationTargets(fallbackToActive:=True)
        If targets.Count = 0 Then
            UpdateStatus("请选择要停止的任务，或等待任务开始运行。")
            Return
        End If
        Dim changed = 0
        For Each item In targets
            Select Case item.State
                Case SampleTaskState.Pending
                    SetTaskState(item, SampleTaskState.Stopped)
                    SetBottomLine(item, "任务未启动即被停止；可重置后重新评估", ColorWarning)
                    changed += 1
                Case SampleTaskState.Running, SampleTaskState.Paused
                    SetTaskState(item, SampleTaskState.Stopping)
                    item.Cancellation?.Cancel()
                    changed += 1
            End Select
        Next
        If changed > 0 Then
            UpdateStatus($"正在停止 {changed} 个任务…")
            RefreshRows(targets)
        End If
        RefreshActionButtons()
    End Sub

    Private Sub PauseOrResumeTasks(sender As Object, e As EventArgs)
        Dim targets = GetOperationTargets(fallbackToActive:=True)
        Dim activeTargets = targets.Where(
            Function(item) item.State = SampleTaskState.Running OrElse
                           item.State = SampleTaskState.Paused).ToList()
        If activeTargets.Count = 0 Then
            UpdateStatus("请选择正在运行或已暂停的任务。")
            Return
        End If
        Dim shouldPause = activeTargets.Any(Function(item) item.State = SampleTaskState.Running)
        Dim changed = 0
        Dim lastError = String.Empty
        For Each item In activeTargets
            If item.Runner Is Nothing Then Continue For
            Dim errorMessage As String = Nothing
            If shouldPause AndAlso item.State = SampleTaskState.Running Then
                If item.Runner.TryPause(errorMessage) Then
                    SetTaskState(item, SampleTaskState.Paused)
                    changed += 1
                Else
                    lastError = errorMessage
                End If
            ElseIf Not shouldPause AndAlso item.State = SampleTaskState.Paused Then
                If item.Runner.TryResume(errorMessage) Then
                    SetTaskState(item, SampleTaskState.Running)
                    changed += 1
                Else
                    lastError = errorMessage
                End If
            End If
        Next
        If changed > 0 Then
            UpdateStatus(If(shouldPause,
                            $"已暂停 {changed} 个运行任务；其他状态的任务未受影响。",
                            $"已恢复 {changed} 个暂停任务；其他状态的任务未受影响。"))
            RefreshRows(activeTargets)
        ElseIf lastError <> "" Then
            UpdateStatus("无法切换状态：" & CompactMessage(lastError))
        Else
            UpdateStatus("所选任务中没有可执行此操作的活动任务。")
        End If
        RefreshActionButtons()
    End Sub

    Private Sub RemoveTasks(sender As Object, e As EventArgs)
        Dim targets = GetSelectedQueueItems()
        If targets.Count = 0 Then
            UpdateStatus("请先选择要移除的任务。")
            Return
        End If
        If targets.Any(Function(item) item.State = SampleTaskState.Running OrElse
                                       item.State = SampleTaskState.Paused OrElse
                                       item.State = SampleTaskState.Stopping) Then
            UpdateStatus("运行中、已暂停或正在停止的任务不能直接移除；请先停止。")
            Return
        End If
        _fileList.BeginUpdate()
        Try
            For Each item In targets
                _fileList.Items.Remove(item.Row)
                _items.Remove(item)
            Next
        Finally
            _fileList.EndUpdate()
        End Try
        UpdateStatus($"已移除 {targets.Count} 个任务。")
        RefreshActionButtons()
    End Sub

    Private Sub ResetTasks(sender As Object, e As EventArgs)
        Dim targets = GetSelectedQueueItems()
        If targets.Count = 0 Then
            UpdateStatus("请先选择要重置状态的任务。")
            Return
        End If
        Dim changed = 0
        For Each item In targets
            If item.State = SampleTaskState.Running OrElse
               item.State = SampleTaskState.Paused OrElse
               item.State = SampleTaskState.Stopping Then Continue For
            item.Result = Nothing
            item.ErrorMessage = String.Empty
            item.CommandLine = String.Empty
            For index = 2 To 5
                item.Row.SubItems(index).Text = "—"
            Next
            SetTaskState(item, SampleTaskState.Pending)
            SetBottomLine(item, item.Path, ColorMuted)
            changed += 1
        Next
        If changed > 0 Then
            RefreshRows(targets)
            UpdateStatus($"已重置 {changed} 个任务；运行中的队列会自动继续处理。")
        Else
            UpdateStatus("所选任务正在运行，无法重置。")
        End If
        RefreshActionButtons()
    End Sub

    Private Sub QueueKeyDown(sender As Object, e As KeyEventArgs)
        If e.KeyCode = Keys.Delete Then
            RemoveTasks(sender, EventArgs.Empty)
            e.Handled = True
        End If
    End Sub

    Private Sub QueueMouseUp(sender As Object, e As MouseEventArgs)
        If e.Button <> MouseButtons.Right Then Return
        _contextMenuTarget = Nothing
        Dim hitIndex = _fileList.HitTest(e.X, e.Y)
        If hitIndex >= 0 Then
            _contextMenuTarget = TryCast(_fileList.Items(hitIndex).Tag, SampleQueueFileItem)
            If Not _fileList.SelectedIndices.Contains(hitIndex) Then _fileList.SelectedIndex = hitIndex
        End If
        RebuildTaskContextMenu()
        If _taskContextMenu.Items.Count > 0 Then _taskContextMenu.Show(_fileList, e.X, e.Y)
    End Sub

    Private Sub RebuildTaskContextMenu()
        _taskContextMenu.Items.Clear()
        Dim selected = GetSelectedQueueItems()
        Dim targets = GetOperationTargets(fallbackToActive:=True)
        Dim activeTargets = targets.Where(
            Function(item) item.State = SampleTaskState.Running OrElse
                           item.State = SampleTaskState.Paused).ToList()

        If _contextMenuTarget IsNot Nothing Then
            AddTaskContextMenuItem("复制此任务的完整命令行", AddressOf CopyTaskCommandLine)
        End If
        Dim hasLifecycleAction = (Not _running AndAlso _items.Any(Function(item) item.State = SampleTaskState.Pending)) OrElse
                                 activeTargets.Count > 0 OrElse
                                 targets.Any(Function(item) item.State = SampleTaskState.Pending OrElse
                                                           item.State = SampleTaskState.Running OrElse
                                                           item.State = SampleTaskState.Paused)
        If hasLifecycleAction AndAlso _taskContextMenu.Items.Count > 0 Then AddTaskContextMenuSeparator()
        If Not _running AndAlso _items.Any(Function(item) item.State = SampleTaskState.Pending) Then
            AddTaskContextMenuItem("开始样本编码", AddressOf StartQueue)
        End If
        If activeTargets.Any(Function(item) item.State = SampleTaskState.Running) Then
            AddTaskContextMenuItem("暂停", AddressOf PauseOrResumeTasks)
        ElseIf activeTargets.Count > 0 Then
            AddTaskContextMenuItem("恢复", AddressOf PauseOrResumeTasks)
        End If
        If targets.Any(Function(item) item.State = SampleTaskState.Pending OrElse
                                      item.State = SampleTaskState.Running OrElse
                                      item.State = SampleTaskState.Paused) Then
            AddTaskContextMenuItem("停止", AddressOf StopTasks, danger:=True)
        End If
        Dim canReset = selected.Any(
            Function(item) item.State <> SampleTaskState.Running AndAlso
                           item.State <> SampleTaskState.Paused AndAlso
                           item.State <> SampleTaskState.Stopping)
        Dim canRemove = selected.Count > 0 AndAlso
                        Not selected.Any(
                            Function(item) item.State = SampleTaskState.Running OrElse
                                           item.State = SampleTaskState.Paused OrElse
                                           item.State = SampleTaskState.Stopping)
        If canReset OrElse canRemove Then
            If _taskContextMenu.Items.Count > 0 Then AddTaskContextMenuSeparator()
            If canReset Then AddTaskContextMenuItem("重置所选任务状态", AddressOf ResetTasks)
            If canRemove Then AddTaskContextMenuItem("移除所选任务", AddressOf RemoveTasks)
        End If
    End Sub

    Private Async Sub CopyTaskCommandLine(sender As Object, e As EventArgs)
        Dim item = _contextMenuTarget
        If item Is Nothing OrElse Not _items.Contains(item) Then
            UpdateStatus("未找到要复制命令行的任务。")
            Return
        End If
        Try
            Dim commandLine = item.CommandLine
            If String.IsNullOrWhiteSpace(commandLine) AndAlso item.Runner IsNot Nothing Then
                commandLine = item.Runner.CurrentCommandLine
            End If
            If String.IsNullOrWhiteSpace(commandLine) Then
                Dim profile = PresetProfile.Load(_presetPath.Text.Trim())
                Dim settings = ReadSettings()
                commandLine = Await AbAv1Runner.BuildSampleCommandLineAsync(
                    profile,
                    item.Path,
                    settings,
                    _lifetimeCancellation.Token)
            End If
            CopyCommandLineToClipboard(
                commandLine,
                $"已复制 {Path.GetFileName(item.Path)} 的完整 sample-encode 命令行。")
        Catch ex As OperationCanceledException When _lifetimeCancellation.IsCancellationRequested
        Catch ex As Exception
            ShowCopyCommandLineError(ex)
        End Try
    End Sub

    Private Sub CopyCommandLineToClipboard(commandLine As String, successMessage As String)
        If String.IsNullOrWhiteSpace(commandLine) Then Throw New InvalidOperationException("生成的命令行为空。")
        Clipboard.SetText(commandLine)
        UpdateStatus(successMessage)
    End Sub

    Private Sub ShowCopyCommandLineError(exception As Exception)
        Dim message = "无法复制命令行：" & CompactMessage(exception.Message)
        UpdateStatus(message)
        MessageBox.Show(FindForm(), exception.Message, "复制 ab-av1 命令行", MessageBoxButtons.OK, MessageBoxIcon.Warning)
    End Sub

    Private Sub AddTaskContextMenuItem(text As String,
                                       handler As EventHandler,
                                       Optional danger As Boolean = False)
        Dim item As New ModernContextMenu.ModernMenuItem(text) With {
            .Font = Nothing,
            .ForeColor = If(danger, ColorDanger, ColorText),
            .CloseOnClick = True
        }
        AddHandler item.Click, handler
        _taskContextMenu.Items.Add(item)
    End Sub

    Private Sub AddTaskContextMenuSeparator()
        _taskContextMenu.Items.Add(New ModernContextMenu.ModernMenuItem With {.IsSeparator = True})
    End Sub

    Private Sub QueueSelectionChanged(sender As Object, e As EventArgs)
        RefreshActionButtons()
    End Sub

    Private Function GetSelectedQueueItems() As List(Of SampleQueueFileItem)
        Dim result As New List(Of SampleQueueFileItem)()
        For Each row In _fileList.SelectedItems
            Dim item = TryCast(row.Tag, SampleQueueFileItem)
            If item IsNot Nothing AndAlso _items.Contains(item) Then result.Add(item)
        Next
        Return result
    End Function

    Private Function GetOperationTargets(fallbackToActive As Boolean) As List(Of SampleQueueFileItem)
        Dim selected = GetSelectedQueueItems()
        If selected.Count = 0 AndAlso fallbackToActive AndAlso _activeItem IsNot Nothing Then selected.Add(_activeItem)
        Return selected
    End Function

    Private Function ReadSettings() As SampleEncodeSettings
        Dim crf As Double
        If Not TryParseNumber(_crf.Text, crf) Then Throw New FormatException("CRF 不是有效数字。")
        Dim sampleCount As Integer? = Nothing
        If Not String.IsNullOrWhiteSpace(_samples.Text) Then
            Dim parsed As Integer
            If Not Integer.TryParse(_samples.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, parsed) Then
                Throw New FormatException("采样数量不是有效整数。")
            End If
            sampleCount = parsed
        End If
        Dim settings As New SampleEncodeSettings With {
            .Metric = GetSelectedMetric(),
            .Crf = crf,
            .Samples = sampleCount,
            .SampleDuration = _sampleDuration.Text.Trim(),
            .VmafModel = _vmafModel.Text.Trim()
        }
        settings.Validate()
        Return settings
    End Function

    Private Sub SetRunning(value As Boolean)
        _running = value
        _startButton.Enabled = Not value
        _presetPath.Enabled = Not value
        _scoreMetric.Enabled = Not value
        _crf.Enabled = Not value
        _samples.Enabled = Not value
        _sampleDuration.Enabled = Not value
        Dim useVmaf = GetSelectedMetric() = QualityMetric.Vmaf
        _vmafModel.Enabled = Not value AndAlso useVmaf
        _refreshModelsButton.Enabled = Not value AndAlso useVmaf AndAlso Not _scanningModels
        _browseModelButton.Enabled = Not value AndAlso useVmaf
        _addFilesButton.Enabled = True
        _progressRing.Visible = value
        If value Then
            _progressRing.StartAnimation()
        Else
            _progressRing.StopAnimation()
        End If
        RefreshActionButtons()
    End Sub

    Private Sub RefreshActionButtons()
        If IsDisposed Then Return
        Dim selected = GetSelectedQueueItems()
        Dim targets = If(selected.Count > 0,
                         selected,
                         If(_activeItem Is Nothing,
                            New List(Of SampleQueueFileItem)(),
                            New List(Of SampleQueueFileItem) From {_activeItem}))
        _stopButton.Enabled = targets.Any(Function(item) item.State = SampleTaskState.Pending OrElse
                                                         item.State = SampleTaskState.Running OrElse
                                                         item.State = SampleTaskState.Paused)
        Dim activeTargets = targets.Where(
            Function(item) item.State = SampleTaskState.Running OrElse
                           item.State = SampleTaskState.Paused).ToList()
        _pauseResumeButton.Enabled = activeTargets.Count > 0
        If activeTargets.Any(Function(item) item.State = SampleTaskState.Running) Then
            _pauseResumeButton.Text = "暂停"
        ElseIf activeTargets.Count > 0 Then
            _pauseResumeButton.Text = "恢复"
        Else
            _pauseResumeButton.Text = "暂停 / 恢复"
        End If
        _removeButton.Enabled = selected.Count > 0 AndAlso
                                Not selected.Any(Function(item) item.State = SampleTaskState.Running OrElse
                                                               item.State = SampleTaskState.Paused OrElse
                                                               item.State = SampleTaskState.Stopping)
        _resetButton.Enabled = selected.Any(Function(item) item.State <> SampleTaskState.Running AndAlso
                                                           item.State <> SampleTaskState.Paused AndAlso
                                                           item.State <> SampleTaskState.Stopping)
    End Sub

    Private Sub SetTaskState(item As SampleQueueFileItem, state As SampleTaskState)
        item.State = state
        Dim text As String
        Dim color As Color
        Select Case state
            Case SampleTaskState.Pending
                text = "等待"
                color = ColorMuted
            Case SampleTaskState.Running
                text = "样本编码中"
                color = ColorAccent
            Case SampleTaskState.Paused
                text = "已暂停"
                color = ColorWarning
            Case SampleTaskState.Stopping
                text = "正在停止"
                color = ColorWarning
            Case SampleTaskState.Completed
                text = "已完成"
                color = ColorSuccess
            Case SampleTaskState.Failed
                text = "失败"
                color = ColorDanger
            Case SampleTaskState.Stopped
                text = "已停止"
                color = ColorWarning
            Case Else
                text = state.ToString()
                color = ColorMuted
        End Select
        item.Row.SubItems(1).Text = text
        item.Row.SubItems(1).ForeColor = color
    End Sub

    Private Sub SetBottomLine(item As SampleQueueFileItem, text As String, color As Color)
        item.Row.BottomLines.Clear()
        item.Row.BottomLines.Add(New UltraDetailListView.TextLine(text, _detailFont, color))
    End Sub

    Private Sub QueueProgressRender(item As SampleQueueFileItem, update As SearchProgress)
        If IsDisposed OrElse item Is Nothing OrElse Not _items.Contains(item) Then Return
        If item.State <> SampleTaskState.Running AndAlso item.State <> SampleTaskState.Paused Then Return
        _pendingProgressItem = item
        _pendingProgressStatus = $"{Path.GetFileName(item.Path)} · {CompactMessage(update.Message)}"
        If update.TestedScore.HasValue Then
            Dim metric = If(update.Metric.HasValue, update.Metric.Value, GetSelectedMetric())
            _pendingProgressScore = FormatMetricScore(metric, update.TestedScore.Value)
        End If
        If Not _progressRenderTimer.Enabled Then _progressRenderTimer.Start()
    End Sub

    Private Sub ProgressRenderTimerTick(sender As Object, e As EventArgs)
        _progressRenderTimer.Stop()
        FlushPendingProgressRender()
    End Sub

    Private Sub FlushPendingProgressRender()
        If IsDisposed Then Return
        Dim item = _pendingProgressItem
        Dim statusText = _pendingProgressStatus
        Dim scoreText = _pendingProgressScore
        _pendingProgressItem = Nothing
        _pendingProgressStatus = Nothing
        _pendingProgressScore = Nothing
        If statusText IsNot Nothing Then UpdateStatus(statusText)
        If item Is Nothing OrElse Not _items.Contains(item) Then Return
        If item.State <> SampleTaskState.Running AndAlso item.State <> SampleTaskState.Paused Then Return
        If scoreText IsNot Nothing AndAlso item.Row.SubItems(3).Text <> scoreText Then
            item.Row.SubItems(3).Text = scoreText
            RefreshRows({item})
        End If
    End Sub

    Private Sub ClearPendingProgress(item As SampleQueueFileItem)
        If Not Object.ReferenceEquals(_pendingProgressItem, item) Then Return
        If Not IsDisposed Then _progressRenderTimer.Stop()
        _pendingProgressItem = Nothing
        _pendingProgressStatus = Nothing
        _pendingProgressScore = Nothing
    End Sub

    Private Sub RefreshRows(items As IEnumerable(Of SampleQueueFileItem))
        If IsDisposed OrElse items Is Nothing Then Return
        Dim targets = items.Where(Function(item) item IsNot Nothing AndAlso _items.Contains(item)).Distinct().ToList()
        If targets.Count = 0 Then Return
        _fileList.BeginUpdate()
        Try
            For Each item In targets
                item.Row.InvalidateCache()
            Next
        Finally
            _fileList.EndUpdate()
        End Try
    End Sub

    Private Sub UpdateStatus(message As String)
        If IsDisposed Then Return
        Dim text = CompactMessage(message)
        If Not String.Equals(_status.Text, text, StringComparison.Ordinal) Then _status.Text = text
    End Sub

    Private Shared Function CompactMessage(message As String) As String
        If String.IsNullOrWhiteSpace(message) Then Return String.Empty
        Dim compact = String.Join(" ", message.Split({ControlChars.Cr, ControlChars.Lf}, StringSplitOptions.RemoveEmptyEntries)).Trim()
        If compact.Length > 260 Then compact = compact.Substring(0, 257) & "…"
        Return compact
    End Function

    Private Shared Function TryParseNumber(text As String, ByRef value As Double) As Boolean
        Return Double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, value) OrElse
               Double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, value)
    End Function

    Private Shared Function FormatMetricScore(metric As QualityMetric, score As Double) As String
        Return $"{GetMetricDisplayName(metric)} {score.ToString("0.###", CultureInfo.InvariantCulture)}"
    End Function

    Private Shared Function FormatDuration(seconds As Double) As String
        If Double.IsNaN(seconds) OrElse Double.IsInfinity(seconds) OrElse seconds <= 0 Then Return "—"
        Dim duration = TimeSpan.FromSeconds(seconds)
        If duration.TotalDays >= 1 Then Return $"{CInt(Math.Floor(duration.TotalDays))}天 {duration:hh\:mm\:ss}"
        If duration.TotalHours >= 1 Then Return duration.ToString("h\:mm\:ss", CultureInfo.InvariantCulture)
        Return duration.ToString("m\:ss", CultureInfo.InvariantCulture)
    End Function

    Private Shared Function CreateTaskContextMenu() As ModernContextMenu
        Return New ModernContextMenu With {
            .AnimationFPS = 120,
            .BackdropBlurPasses = 2,
            .BackdropBlurRadius = 30,
            .BackdropMode = ModernContextMenu.BackdropModeEnum.Auto,
            .BackdropNoiseOpacity = 0,
            .BackdropTintColor = Color.FromArgb(40, 0, 0, 0),
            .BackColor1 = ColorBackground,
            .BorderColor = Color.FromArgb(80, 220, 220, 220),
            .BorderSize = 1,
            .HoverBackColor = ColorControlHover,
            .PressedBackColor = ColorControlPressed,
            .HoverRadius = 6,
            .IconSize = 0,
            .ItemHeight = 38,
            .ItemPadding = New Padding(14, 0, 14, 0),
            .MenuFont = New Font("Microsoft YaHei UI", 10.0F),
            .MenuForeColor = ColorText,
            .MenuPadding = New Padding(10),
            .SeparatorColor = Color.FromArgb(80, 220, 220, 220),
            .SeparatorHeight = 14
        }
    End Function

    Private Shared Function CreateTextBox(waterText As String) As ModernTextBox
        Return New ModernTextBox With {
            .Dock = DockStyle.Fill,
            .Margin = New Padding(0, 5, 0, 5),
            .Padding = New Padding(10, 0, 10, 0),
            .BackColor1 = ColorControl,
            .ForeColor = ColorText,
            .WaterText = waterText,
            .WaterTextForeColor = ColorMuted,
            .CaretColor = ColorText,
            .SelectionColor = ColorControl,
            .BorderColor = Color.Transparent,
            .BorderColorFocus = ColorControlPressed,
            .BorderSize = 0,
            .BorderRadius = 10,
            .MultiLine = False
        }
    End Function

    Private Shared Function CreateComboBox(waterText As String) As ModernComboBox
        Return New ModernComboBox With {
            .Dock = DockStyle.Fill,
            .Margin = New Padding(0, 5, 0, 5),
            .Padding = New Padding(10, 0, 10, 0),
            .BackColor1 = ColorControl,
            .BackColor2 = ColorControl,
            .HoverBackColor1 = ColorControlHover,
            .HoverBackColor2 = ColorControlHover,
            .PressedBackColor1 = ColorControlPressed,
            .PressedBackColor2 = ColorControlPressed,
            .ForeColor = ColorText,
            .WaterText = waterText,
            .WaterTextForeColor = ColorMuted,
            .CaretColor = ColorText,
            .SelectionColor = ColorControl,
            .ArrowColor = ColorMuted,
            .BorderColor = Color.Transparent,
            .BorderColorFocus = ColorControlPressed,
            .BorderSize = 0,
            .BorderRadius = 10,
            .Editable = True,
            .MaxDropDownItems = 12
        }
    End Function

    Private Shared Function CreateMetricComboBox() As ModernComboBox
        Dim combo = CreateComboBox("选择评分指标")
        combo.Editable = False
        combo.Items.Add("VMAF")
        combo.Items.Add("XPSNR")
        combo.SelectedIndex = 0
        Return combo
    End Function

    Private Shared Function CreateButton(text As String,
                                         handler As EventHandler,
                                         Optional accent As Boolean = False,
                                         Optional danger As Boolean = False) As ModernButton
        Dim baseColor = If(accent,
                           Color.FromArgb(80, 71, 156, 255),
                           If(danger, Color.FromArgb(40, 235, 93, 93), ColorControl))
        Dim hoverColor = If(accent,
                            Color.FromArgb(110, 71, 156, 255),
                            If(danger, Color.FromArgb(60, 235, 93, 93), ColorControlHover))
        Dim pressedColor = If(accent,
                              Color.FromArgb(140, 71, 156, 255),
                              If(danger, Color.FromArgb(80, 235, 93, 93), ColorControlPressed))
        Dim button As New ModernButton With {
            .Text = text,
            .Font = New Font("Microsoft YaHei UI", 10.0F),
            .ForeColor = If(danger, ColorDanger, ColorText),
            .BackColor1 = baseColor,
            .BackColor2 = baseColor,
            .HoverBackColor1 = hoverColor,
            .HoverBackColor2 = hoverColor,
            .PressedBackColor1 = pressedColor,
            .PressedBackColor2 = pressedColor,
            .BorderColor = Color.Transparent,
            .HoverBorderColor = Color.Transparent,
            .BorderSize = 0,
            .BorderRadius = 10,
            .Size = New Size(160, 38),
            .Margin = New Padding(0, 6, 12, 6)
        }
        AddHandler button.Click, handler
        Return button
    End Function

    Private Shared Function CreateFileList() As UltraDetailListView
        Dim list As New UltraDetailListView With {
            .Dock = DockStyle.Fill,
            .Margin = Padding.Empty,
            .BackgroundColor = Color.Transparent,
            .BorderColor = Color.Transparent,
            .BorderSize = 0,
            .BorderRadius = 0,
            .HeaderVisible = True,
            .HeaderHeight = 40,
            .HeaderBackColor = ColorControl,
            .HeaderForeColor = ColorText,
            .HeaderBorderColor = Color.Transparent,
            .HeaderBorderWidth = 0,
            .ContentPadding = New Padding(0, 8, 0, 8),
            .ItemPadding = New Padding(14, 11, 14, 11),
            .ItemSpacing = 6,
            .ItemCornerRadius = 10,
            .ItemForeColor = ColorText,
            .ItemHoverBackColor = ColorControl,
            .ItemSelectedBackColor = ColorControlHover,
            .MultiSelect = True,
            .AllowColumnResize = True,
            .AllowDragReorder = False,
            .WordWrap = False,
            .BottomLinesSpacing = 4,
            .TextLineSpacing = 2,
            .ScrollBarWidth = 8,
            .ScrollBarTrackColor = Color.Transparent,
            .ScrollBarThumbColor = ColorControl,
            .ScrollBarThumbHoverColor = ColorControlHover,
            .SelectionRectFillColor = Color.FromArgb(20, 71, 156, 255),
            .SelectionRectBorderColor = Color.FromArgb(80, 71, 156, 255),
            .Font = New Font("Microsoft YaHei UI", 10.0F)
        }
        list.Columns.Add(New UltraDetailListView.ListColumn("文件", 480))
        list.Columns.Add(New UltraDetailListView.ListColumn("状态", 120))
        list.Columns.Add(New UltraDetailListView.ListColumn("CRF", 80))
        list.Columns.Add(New UltraDetailListView.ListColumn("分数", 130))
        list.Columns.Add(New UltraDetailListView.ListColumn("预测大小", 130))
        list.Columns.Add(New UltraDetailListView.ListColumn("预测耗时", 130))
        Return list
    End Function

    Private Shared Function CreateSectionHeading(title As String, description As String) As HtmlColorLabel
        Return New HtmlColorLabel With {
            .Dock = DockStyle.Fill,
            .Margin = Padding.Empty,
            .Padding = Padding.Empty,
            .BackColor1 = Color.Transparent,
            .BorderSize = 0,
            .ForeColor = ColorMuted,
            .Text = $"<span style=""font-size:13; color:Silver"">{title}</span>   {description}",
            .TextAlign = HtmlColorLabel.TextAlignEnum.MiddleLeft
        }
    End Function

    Private Shared Function CreateFieldLabel(text As String) As HtmlColorLabel
        Dim label = CreateLabel(text, ColorMuted, 9.0F)
        label.Dock = DockStyle.Fill
        label.TextAlign = HtmlColorLabel.TextAlignEnum.MiddleLeft
        label.Margin = New Padding(2, 0, 2, 0)
        Return label
    End Function

    Private Shared Function CreateLabel(text As String,
                                        color As Color,
                                        size As Single,
                                        Optional style As FontStyle = FontStyle.Regular) As HtmlColorLabel
        Return New HtmlColorLabel With {
            .Text = text,
            .ForeColor = color,
            .BackColor = Color.Transparent,
            .BackColor1 = Color.Transparent,
            .BorderSize = 0,
            .Font = New Font("Microsoft YaHei UI", size, style),
            .TextAlign = HtmlColorLabel.TextAlignEnum.TopLeft
        }
    End Function

    Private Enum SampleTaskState
        Pending
        Running
        Paused
        Stopping
        Completed
        Failed
        Stopped
    End Enum

    Private NotInheritable Class SampleQueueFileItem
        Public Sub New(path As String, row As UltraDetailListView.ListItem)
            Me.Path = path
            Me.Row = row
        End Sub

        Public ReadOnly Property Path As String
        Public ReadOnly Property Row As UltraDetailListView.ListItem
        Public Property State As SampleTaskState = SampleTaskState.Pending
        Public Property Runner As AbAv1Runner
        Public Property Cancellation As CancellationTokenSource
        Public Property Result As SampleEncodeResult
        Public Property ErrorMessage As String = String.Empty
        Public Property CommandLine As String = String.Empty
    End Class

End Class
