Imports System.Drawing
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports LakeUI

Public NotInheritable Class MainPanel
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
    Private Const SearchHeadingHeight As Single = 44.0F
    Private Const SearchParameterRowHeight As Single = 82.0F
    Private Const SearchModelHeight As Single = 74.0F
    Private Const SearchVerticalPadding As Single = 20.0F
    Private Const CompactSearchWidth As Single = 1180.0F

    Private ReadOnly _presetPath As ModernTextBox
    Private ReadOnly _outputDirectory As ModernTextBox
    Private ReadOnly _scoreMetric As ModernComboBox
    Private ReadOnly _targetScore As ModernTextBox
    Private ReadOnly _minCrf As ModernTextBox
    Private ReadOnly _maxCrf As ModernTextBox
    Private ReadOnly _samples As ModernTextBox
    Private ReadOnly _sampleDuration As ModernTextBox
    Private ReadOnly _thorough As ModernCheckBox
    Private ReadOnly _vmafModel As ModernComboBox
    Private ReadOnly _fileList As UltraDetailListView
    Private ReadOnly _presetSummary As Label
    Private ReadOnly _environmentStatus As Label
    Private ReadOnly _vmafModelStatus As Label
    Private ReadOnly _status As Label
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
    Private ReadOnly _tabControl As ModernTabControl
    Private ReadOnly _sampleEncodePanel As SampleEncodePanel
    Private ReadOnly ModernPanel1 As ModernPanel
    Private _vmafModelRow As TableLayoutPanel
    Private _searchParametersLayout As TableLayoutPanel
    Private _searchFields As Control()
    Private _searchRootRowStyle As RowStyle
    Private _searchParameterRowsStyle As RowStyle
    Private _searchModelRowStyle As RowStyle
    Private _searchLayoutCompact As Boolean?

    Private ReadOnly _items As New List(Of QueueFileItem)()
    Private ReadOnly _lifetimeCancellation As New CancellationTokenSource()
    Private _activeItem As QueueFileItem
    Private _contextMenuTarget As QueueFileItem
    Private _schedulerTask As Task
    Private _running As Boolean
    Private _scanningModels As Boolean
    Private _initialModelScanStarted As Boolean
    Private _pendingProgressItem As QueueFileItem
    Private _pendingProgressStatus As String
    Private _pendingProgressCrf As String
    Private _pendingProgressScore As String
    Private _previousMetric As QualityMetric = QualityMetric.Vmaf

    Public Sub New()
        SuspendLayout()
        DoubleBuffered = True
        BackColor = ColorBackground
        ForeColor = ColorText
        Font = New Font("Microsoft YaHei UI", 10.0F)
        _detailFont = New Font("Microsoft YaHei UI", 8.5F)
        Dock = DockStyle.Fill
        AllowDrop = True
        MinimumSize = New Size(900, 720)
        Padding = Padding.Empty

        _presetPath = CreateTextBox("选择 FFmpegFreeUI v6 JSON 预设")
        _outputDirectory = CreateTextBox("留空则输出到输入文件目录")
        _scoreMetric = CreateMetricComboBox()
        _targetScore = CreateTextBox("95")
        _targetScore.Text = "95"
        _minCrf = CreateTextBox("5")
        _minCrf.Text = "5"
        _maxCrf = CreateTextBox("55")
        _maxCrf.Text = "55"
        _samples = CreateTextBox("留空使用 ab-av1 自动采样")
        _sampleDuration = CreateTextBox("20s")
        _sampleDuration.Text = "20s"
        _vmafModel = CreateComboBox("留空使用 ab-av1 自动模型；也可直接输入模型名称")

        _thorough = New ModernCheckBox With {
            .Text = "彻底搜索",
            .SubText = "搜索到更贴近目标值",
            .ForeColor = ColorText,
            .SubTextForeColor = ColorMuted,
            .Checked = False,
            .ClickAnywhere = True,
            .Dock = DockStyle.Fill,
            .Margin = New Padding(10, 4, 0, 4)
        }

        _fileList = CreateFileList()
        _fileList.AllowDrop = True
        _presetSummary = CreateLabel("尚未读取预设", ColorMuted, 9.0F)
        _presetSummary.AutoEllipsis = True
        _environmentStatus = CreateLabel(String.Empty, ColorMuted, 9.0F)
        _environmentStatus.AutoEllipsis = True
        _vmafModelStatus = CreateLabel("等待扫描当前 ffmpeg", ColorMuted, 8.5F)
        _vmafModelStatus.AutoEllipsis = True
        _status = CreateLabel("就绪", ColorMuted, 9.0F)
        _status.AutoEllipsis = True

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
        _startButton = CreateButton("开始搜索队列", AddressOf StartQueue, accent:=True)
        _taskContextMenu = CreateTaskContextMenu()
        _progressRenderTimer = New System.Windows.Forms.Timer With {
            .Interval = ProgressRenderIntervalMilliseconds
        }
        AddHandler _progressRenderTimer.Tick, AddressOf ProgressRenderTimerTick
        _sampleEncodePanel = New SampleEncodePanel With {
            .Dock = DockStyle.Fill,
            .Margin = Padding.Empty,
            .BackColor = Color.Transparent
        }
        _tabControl = CreatePageTabs(BuildLayout(), _sampleEncodePanel)

        '3FUI 会寻找名为 ModernPanel1 且 Dock=Fill 的 LakeUI.ModernPanel，
        '并在启用个性化背景时自动设置透明背景和 BackgroundSource。
        ModernPanel1 = New ModernPanel With {
            .Name = "ModernPanel1",
            .Dock = DockStyle.Fill,
            .Margin = Padding.Empty,
            .Padding = New Padding(24, 20, 24, 18),
            .BackColor = Color.Transparent,
            .BackColor1 = ColorBackground,
            .BorderSize = 0,
            .BorderRadius = 0,
            .AllowDrop = True
        }
        ModernPanel1.SuspendLayout()
        ModernPanel1.Controls.Add(_tabControl)
        Controls.Add(ModernPanel1)

        AddHandler _presetPath.LostFocus, AddressOf PresetPathLostFocus
        AddHandler _scoreMetric.SelectedIndexChanged, AddressOf ScoreMetricChanged
        AddHandler _fileList.SelectedIndexChanged, AddressOf QueueSelectionChanged
        AddHandler _fileList.KeyDown, AddressOf QueueKeyDown
        AddHandler _fileList.MouseUp, AddressOf QueueMouseUp
        AddHandler Me.Load, AddressOf MainPanelLoad
        AddHandler Me.DragEnter, AddressOf FilesDragEnter
        AddHandler Me.DragOver, AddressOf FilesDragOver
        AddHandler Me.DragDrop, AddressOf FilesDragDrop
        AddHandler ModernPanel1.DragEnter, AddressOf FilesDragEnter
        AddHandler ModernPanel1.DragOver, AddressOf FilesDragOver
        AddHandler ModernPanel1.DragDrop, AddressOf FilesDragDrop
        AddHandler _fileList.DragEnter, AddressOf FilesDragEnter
        AddHandler _fileList.DragOver, AddressOf FilesDragOver
        AddHandler _fileList.DragDrop, AddressOf FilesDragDrop

        LoadDefaults()
        RefreshEnvironmentStatus()
        RefreshPresetSummary()
        UpdateScoreMetricUi(adjustDefaultScore:=False)
        RefreshActionButtons()
        ModernPanel1.ResumeLayout(False)
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
            '异步搜索会在自己的 Finally 中释放任务 CTS。这里不能与仍在收尾的任务并发 Dispose。
            _taskContextMenu.Dispose()
            _detailFont.Dispose()
        End If
        MyBase.Dispose(disposing)
    End Sub

    Private Async Sub MainPanelLoad(sender As Object, e As EventArgs)
        If _initialModelScanStarted Then Return
        _initialModelScanStarted = True
        If GetSelectedMetric() = QualityMetric.Vmaf Then Await RefreshVmafModelsAsync()
    End Sub

    Private Function BuildLayout() As Control
        Dim root As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 1,
            .RowCount = 4,
            .Margin = Padding.Empty,
            .Padding = Padding.Empty,
            .BackColor = Color.Transparent
        }
        root.SuspendLayout()
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 184))
        _searchRootRowStyle = New RowStyle(SizeType.Absolute, 220)
        root.RowStyles.Add(_searchRootRowStyle)
        root.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 68))

        root.Controls.Add(BuildPathSection(), 0, 0)
        root.Controls.Add(BuildSearchSection(), 0, 1)
        root.Controls.Add(BuildFileSection(), 0, 2)
        root.Controls.Add(BuildFooter(), 0, 3)
        root.ResumeLayout(False)
        Return root
    End Function

    Private Function BuildPathSection() As Control
        Dim layout As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 5,
            .RowCount = 4,
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
        layout.RowStyles.Add(New RowStyle(SizeType.Absolute, 48))
        layout.RowStyles.Add(New RowStyle(SizeType.Absolute, 48))
        layout.RowStyles.Add(New RowStyle(SizeType.Absolute, 40))

        Dim topBar As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 2,
            .RowCount = 1,
            .Margin = Padding.Empty,
            .Padding = Padding.Empty,
            .BackColor = Color.Transparent
        }
        topBar.SuspendLayout()
        topBar.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 70))
        topBar.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 30))
        topBar.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        topBar.Controls.Add(
            CreateSectionHeading("AB-AV1 CRF 搜索", "读取 v6 预设，只替换 CRF，完成后加入 3FUI 原生队列"),
            0,
            0)
        _environmentStatus.Dock = DockStyle.Fill
        _environmentStatus.TextAlign = ContentAlignment.MiddleRight
        topBar.Controls.Add(_environmentStatus, 1, 0)
        topBar.ResumeLayout(False)
        layout.Controls.Add(topBar, 0, 0)
        layout.SetColumnSpan(topBar, 5)

        Dim browsePresetButton = CreateButton("选择预设", AddressOf BrowsePreset)
        Dim browseOutputButton = CreateButton("输出目录", AddressOf BrowseOutputDirectory)
        browsePresetButton.Dock = DockStyle.Fill
        browseOutputButton.Dock = DockStyle.Fill
        browsePresetButton.Margin = New Padding(0, 6, 12, 6)
        browseOutputButton.Margin = New Padding(0, 6, 12, 6)

        layout.Controls.Add(browsePresetButton, 0, 1)
        layout.Controls.Add(_presetPath, 2, 1)
        _copyCommandLineButton.Dock = DockStyle.Fill
        _copyCommandLineButton.Margin = New Padding(0, 6, 0, 6)
        layout.Controls.Add(_copyCommandLineButton, 4, 1)
        layout.Controls.Add(browseOutputButton, 0, 2)
        layout.Controls.Add(_outputDirectory, 2, 2)
        layout.SetColumnSpan(_outputDirectory, 3)

        Dim summaryCaption = CreateLabel("当前预设", ColorMuted, 9.5F)
        summaryCaption.Dock = DockStyle.Fill
        summaryCaption.TextAlign = ContentAlignment.MiddleLeft
        layout.Controls.Add(summaryCaption, 0, 3)
        _presetSummary.Dock = DockStyle.Fill
        _presetSummary.TextAlign = ContentAlignment.MiddleLeft
        layout.Controls.Add(_presetSummary, 2, 3)
        layout.SetColumnSpan(_presetSummary, 3)
        layout.ResumeLayout(False)
        Return layout
    End Function

    Private Function BuildSearchSection() As Control
        Dim layout As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 1,
            .RowCount = 3,
            .Padding = New Padding(0, 10, 0, 10),
            .BackColor = Color.Transparent
        }
        layout.SuspendLayout()
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        layout.RowStyles.Add(New RowStyle(SizeType.Absolute, SearchHeadingHeight))
        _searchParameterRowsStyle = New RowStyle(SizeType.Absolute, SearchParameterRowHeight)
        layout.RowStyles.Add(_searchParameterRowsStyle)
        layout.RowStyles.Add(New RowStyle(SizeType.Absolute, SearchModelHeight))
        _searchModelRowStyle = layout.RowStyles(2)

        Dim heading = CreateSectionHeading("搜索参数", "选择 VMAF 或 XPSNR，设置目标分数、CRF 范围和采样方式")
        layout.Controls.Add(heading, 0, 0)

        _searchParametersLayout = New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .Margin = Padding.Empty,
            .Padding = Padding.Empty,
            .BackColor = Color.Transparent
        }
        _searchFields = {
            CreateSearchField("评分指标", _scoreMetric),
            CreateSearchField("目标分数", _targetScore),
            CreateSearchField("最小 CRF", _minCrf),
            CreateSearchField("最大 CRF", _maxCrf),
            CreateSearchField("采样数量", _samples),
            CreateSearchField("单段时长", _sampleDuration),
            _thorough
        }
        ConfigureSearchParameterLayout(compact:=False, force:=True)
        AddHandler _searchParametersLayout.SizeChanged, AddressOf SearchParametersLayoutSizeChanged
        layout.Controls.Add(_searchParametersLayout, 0, 1)

        _vmafModelRow = New TableLayoutPanel With {
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
        modelCaption.AutoEllipsis = False
        modelCaption.TextAlign = ContentAlignment.MiddleLeft
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
        _vmafModelStatus.TextAlign = ContentAlignment.MiddleLeft
        _vmafModelRow.Controls.Add(_vmafModelStatus, 4, 0)
        _vmafModelRow.ResumeLayout(False)

        layout.Controls.Add(_vmafModelRow, 0, 2)
        layout.ResumeLayout(False)
        Return layout
    End Function

    Private Sub SearchParametersLayoutSizeChanged(sender As Object, e As EventArgs)
        If _searchParametersLayout Is Nothing OrElse _searchParametersLayout.ClientSize.Width <= 0 Then Return
        ConfigureSearchParameterLayout(_searchParametersLayout.ClientSize.Width < CompactSearchWidth)
    End Sub

    Private Sub ConfigureSearchParameterLayout(compact As Boolean, Optional force As Boolean = False)
        If _searchParametersLayout Is Nothing OrElse _searchFields Is Nothing Then Return
        If Not force AndAlso _searchLayoutCompact.HasValue AndAlso _searchLayoutCompact.Value = compact Then Return

        _searchParametersLayout.SuspendLayout()
        Try
            _searchParametersLayout.Controls.Clear()
            _searchParametersLayout.ColumnStyles.Clear()
            _searchParametersLayout.RowStyles.Clear()

            If compact Then
                _searchParametersLayout.ColumnCount = 4
                _searchParametersLayout.RowCount = 2
                For index = 1 To 4
                    _searchParametersLayout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 25.0F))
                Next
                _searchParametersLayout.RowStyles.Add(New RowStyle(SizeType.Percent, 50.0F))
                _searchParametersLayout.RowStyles.Add(New RowStyle(SizeType.Percent, 50.0F))

                For index = 0 To 3
                    _searchParametersLayout.Controls.Add(_searchFields(index), index, 0)
                Next
                _searchParametersLayout.Controls.Add(_searchFields(4), 0, 1)
                _searchParametersLayout.SetColumnSpan(_searchFields(4), 2)
                _searchParametersLayout.Controls.Add(_searchFields(5), 2, 1)
                _searchParametersLayout.Controls.Add(_searchFields(6), 3, 1)
            Else
                _searchParametersLayout.ColumnCount = 7
                _searchParametersLayout.RowCount = 1
                For Each columnWidth As Single In New Single() {12.0F, 12.0F, 12.0F, 12.0F, 22.0F, 14.0F, 16.0F}
                    _searchParametersLayout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, columnWidth))
                Next
                _searchParametersLayout.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
                For index = 0 To _searchFields.Length - 1
                    _searchParametersLayout.Controls.Add(_searchFields(index), index, 0)
                    _searchParametersLayout.SetColumnSpan(_searchFields(index), 1)
                Next
            End If
        Finally
            _searchParametersLayout.ResumeLayout(False)
        End Try

        _searchLayoutCompact = compact
        UpdateSearchSectionHeight()
    End Sub

    Private Sub UpdateSearchSectionHeight()
        If _searchRootRowStyle Is Nothing OrElse
           _searchParameterRowsStyle Is Nothing OrElse
           _searchModelRowStyle Is Nothing Then Return

        Dim parameterHeight = SearchParameterRowHeight * If(_searchLayoutCompact.GetValueOrDefault(), 2.0F, 1.0F)
        Dim modelHeight = If(GetSelectedMetric() = QualityMetric.Vmaf, SearchModelHeight, 0.0F)
        _searchParameterRowsStyle.Height = parameterHeight
        _searchModelRowStyle.Height = modelHeight
        _searchRootRowStyle.Height = SearchHeadingHeight + parameterHeight + modelHeight + SearchVerticalPadding
    End Sub

    Private Function BuildFileSection() As Control
        Dim layout As New TableLayoutPanel With {
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
            CreateSectionHeading("搜索任务", "运行中仍可拖入或添加文件；新任务会进入当前等待队列"),
            0,
            0)

        Dim toolbar As New FlowLayoutPanel With {
            .Dock = DockStyle.Fill,
            .FlowDirection = FlowDirection.LeftToRight,
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
        Dim layout As New TableLayoutPanel With {
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
        _status.TextAlign = ContentAlignment.MiddleLeft
        _startButton.Dock = DockStyle.Fill
        layout.Controls.Add(_progressRing, 0, 0)
        layout.Controls.Add(_status, 1, 0)
        layout.Controls.Add(_startButton, 2, 0)
        layout.ResumeLayout(False)
        Return layout
    End Function

    Private Shared Function CreatePageTabs(crfSearchPage As Control,
                                           sampleEncodePage As Control) As ModernTabControl
        crfSearchPage.Dock = DockStyle.Fill
        crfSearchPage.Margin = Padding.Empty
        sampleEncodePage.Dock = DockStyle.Fill
        sampleEncodePage.Margin = Padding.Empty

        Dim tabs As New ModernTabControl With {
            .Dock = DockStyle.Fill,
            .Margin = Padding.Empty,
            .Padding = Padding.Empty,
            .BackColor = Color.Transparent,
            .ContentBackColor = Color.Transparent,
            .ContentBorderColor = Color.Transparent,
            .ContentBorderWidth = 0,
            .TabStripBackColor = Color.Transparent,
            .TabStripOverlayColor = Color.Transparent,
            .SeparatorColor = Color.Transparent,
            .SeparatorWidth = 0,
            .TabStripHeight = 46,
            .TabStripPadding = New Padding(0, 0, 0, 4),
            .TabSizingMode = ModernTabControl.TabSizingEnum.AutoWidth,
            .TabAlignment = ModernTabControl.TabAlignmentEnum.Left,
            .TabPosition = ModernTabControl.TabPositionEnum.Top,
            .TabItemMinWidth = 112,
            .TabItemSpacing = 4,
            .TabItemTextPadding = 16,
            .TabItemBorderRadius = 8,
            .TabItemForeColor = ColorMuted,
            .TabItemSelectedForeColor = ColorText,
            .TabItemHoverBackColor = ColorControl,
            .TabItemSelectedBackColor = ColorControl,
            .IndicatorColor = ColorAccent,
            .IndicatorHeight = 3,
            .IndicatorPadding = 10,
            .IndicatorBorderRadius = 2,
            .AnimationDuration = 120,
            .AnimationFPS = 60,
            .SuppressBoundPageRefreshOnSwitch = True
        }
        tabs.Items.Add(New ModernTabControl.ModernTab("CRF 搜索") With {
            .BoundControl = crfSearchPage
        })
        tabs.Items.Add(New ModernTabControl.ModernTab("样本编码") With {
            .BoundControl = sampleEncodePage
        })
        tabs.SelectedIndex = 0
        Return tabs
    End Function

    Private Shared Function CreateMetricComboBox() As ModernComboBox
        Dim combo = CreateComboBox("选择评分指标")
        combo.Editable = False
        combo.Items.Add("VMAF")
        combo.Items.Add("XPSNR")
        combo.SelectedIndex = 0
        Return combo
    End Function

    Private Function GetSelectedMetric() As QualityMetric
        Return If(_scoreMetric.SelectedIndex = 1 OrElse
                  String.Equals(_scoreMetric.Text.Trim(), "XPSNR", StringComparison.OrdinalIgnoreCase),
                  QualityMetric.Xpsnr,
                  QualityMetric.Vmaf)
    End Function

    Private Sub ScoreMetricChanged(sender As Object, e As EventArgs)
        UpdateScoreMetricUi(adjustDefaultScore:=True)
    End Sub

    Private Sub UpdateScoreMetricUi(adjustDefaultScore As Boolean)
        If _vmafModelRow Is Nothing OrElse _searchModelRowStyle Is Nothing Then Return

        Dim metric = GetSelectedMetric()
        If adjustDefaultScore AndAlso metric <> _previousMetric Then
            Dim score As Double
            If TryParseNumber(_targetScore.Text, score) Then
                If _previousMetric = QualityMetric.Vmaf AndAlso Math.Abs(score - 95) < 0.0001 Then
                    _targetScore.Text = "42"
                ElseIf _previousMetric = QualityMetric.Xpsnr AndAlso Math.Abs(score - 42) < 0.0001 Then
                    _targetScore.Text = "95"
                End If
            End If
        End If

        Dim showModel = metric = QualityMetric.Vmaf
        _vmafModelRow.Visible = showModel
        _vmafModel.Enabled = showModel AndAlso Not _running
        _refreshModelsButton.Enabled = showModel AndAlso Not _running AndAlso Not _scanningModels
        _browseModelButton.Enabled = showModel AndAlso Not _running
        _previousMetric = metric
        UpdateSearchSectionHeight()
        _vmafModelRow.Parent?.PerformLayout()
    End Sub

    Private Shared Function CreateSearchField(caption As String, editor As Control) As Control
        Dim layout As New TableLayoutPanel With {
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
        label.TextAlign = ContentAlignment.BottomLeft
        layout.Controls.Add(label, 0, 0)
        layout.Controls.Add(editor, 0, 1)
        layout.ResumeLayout(False)
        Return layout
    End Function

    Private Sub LoadDefaults()
        _presetPath.Text = PluginEnvironment.FindDefaultPreset()
    End Sub

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

    Private Sub BrowseOutputDirectory(sender As Object, e As EventArgs)
        If _running Then Return
        Using dialog As New FolderBrowserDialog With {
            .Description = "选择最终编码输出目录",
            .UseDescriptionForTitle = True,
            .ShowNewFolderButton = True
        }
            If Directory.Exists(_outputDirectory.Text.Trim()) Then dialog.InitialDirectory = _outputDirectory.Text.Trim()
            If dialog.ShowDialog(FindForm()) = DialogResult.OK Then _outputDirectory.Text = dialog.SelectedPath
        End Using
    End Sub

    Private Async Sub CopyCommandLineTemplate(sender As Object, e As EventArgs)
        Try
            Dim profile = PresetProfile.Load(_presetPath.Text.Trim())
            Dim settings = ReadSearchSettings()
            Dim commandLine = Await AbAv1Runner.BuildCommandLineTemplateAsync(
                profile,
                settings,
                _lifetimeCancellation.Token)
            CopyCommandLineToClipboard(
                commandLine,
                "已复制当前预设的 ab-av1 命令行模板；<输入文件> 是待替换的路径。")
        Catch ex As OperationCanceledException When _lifetimeCancellation.IsCancellationRequested
        Catch ex As Exception
            ShowCopyCommandLineError(ex)
        End Try
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
            .Title = "选择要搜索 CRF 的媒体文件",
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
                    New UltraDetailListView.ListSubItem("—", Font, ColorMuted)
                })
                Dim item As New QueueFileItem(filePath, row)
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
            If Not _items.Any(Function(item) item.State = SearchTaskState.Pending) Then
                Throw New InvalidOperationException("没有等待中的任务；可添加文件或先重置任务状态。")
            End If

            Dim profile = PresetProfile.Load(_presetPath.Text.Trim())
            Dim settings = ReadSearchSettings()
            Dim outputDirectory = _outputDirectory.Text.Trim()
            If outputDirectory.Length > 0 AndAlso Not Directory.Exists(outputDirectory) Then
                Throw New DirectoryNotFoundException($"输出目录不存在：{outputDirectory}")
            End If

            SetRunning(True)
            _schedulerTask = RunQueueAsync(profile, settings, outputDirectory)
            Await _schedulerTask
        Catch ex As OperationCanceledException When _lifetimeCancellation.IsCancellationRequested
        Catch ex As Exception
            UpdateStatus("无法开始：" & CompactMessage(ex.Message))
            MessageBox.Show(FindForm(), ex.Message, "AB-AV1", MessageBoxButtons.OK, MessageBoxIcon.Warning)
        Finally
            _schedulerTask = Nothing
            If Not IsDisposed Then SetRunning(False)
        End Try
    End Sub

    Private Async Function RunQueueAsync(profile As PresetProfile,
                                         settings As SearchSettings,
                                         outputDirectory As String) As Task
        While Not _lifetimeCancellation.IsCancellationRequested
            Dim item = _items.FirstOrDefault(Function(value) value.State = SearchTaskState.Pending)
            If item Is Nothing Then
                '让运行中刚好发生的拖放事件有机会把新任务加入等待队列。
                Await Task.Yield()
                item = _items.FirstOrDefault(Function(value) value.State = SearchTaskState.Pending)
                If item Is Nothing Then Exit While
            End If
            Await RunQueueItemAsync(item, profile, settings, outputDirectory)
        End While

        If Not IsDisposed Then
            Dim enqueued = _items.Where(Function(value) value.State = SearchTaskState.Enqueued).Count()
            Dim failed = _items.Where(Function(value) value.State = SearchTaskState.Failed).Count()
            Dim stopped = _items.Where(Function(value) value.State = SearchTaskState.Stopped).Count()
            UpdateStatus($"搜索队列结束：{enqueued} 个已入队，{failed} 个失败，{stopped} 个已停止")
        End If
    End Function

    Private Async Function RunQueueItemAsync(item As QueueFileItem,
                                             profile As PresetProfile,
                                             settings As SearchSettings,
                                             outputDirectory As String) As Task
        _activeItem = item
        item.Runner = New AbAv1Runner()
        item.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token)
        SetTaskState(item, SearchTaskState.Running)
        SetBottomLine(item, item.Path, ColorMuted)
        UpdateStatus($"{Path.GetFileName(item.Path)} · 正在启动 ab-av1")
        RefreshRows({item})
        RefreshActionButtons()

        Dim localItem = item
        Dim progress As New Progress(Of SearchProgress)(
            Sub(update)
                If IsDisposed Then Return
                QueueProgressRender(localItem, update)
            End Sub)

        Try
            Dim result = Await item.Runner.SearchAsync(profile, item.Path, settings, progress, item.Cancellation.Token)
            Dim outputPath = PluginEnvironment.BuildOutputPath(item.Path, outputDirectory, profile.OutputContainer, result.Crf)
            Dim temporaryPreset As String = Nothing
            Try
                temporaryPreset = profile.CreateTemporaryPreset(result.Crf)
                Entry.EnqueuePresetTask(
                    temporaryPreset,
                    $"{Path.GetFileName(item.Path)} · CRF {SearchSettings.FormatNumber(result.Crf)}",
                    outputPath,
                    item.Path)
            Finally
                If temporaryPreset IsNot Nothing AndAlso File.Exists(temporaryPreset) Then File.Delete(temporaryPreset)
            End Try

            item.Result = result
            item.OutputPath = outputPath
            item.Row.SubItems(2).Text = SearchSettings.FormatNumber(result.Crf)
            item.Row.SubItems(3).Text = FormatMetricScore(result.Metric, result.Score)
            item.Row.SubItems(4).Text = PluginEnvironment.FormatBytes(result.PredictedEncodeSize)
            SetTaskState(item, SearchTaskState.Enqueued)
            SetBottomLine(item, $"输出：{outputPath}", ColorMuted)
        Catch ex As OperationCanceledException
            SetTaskState(item, SearchTaskState.Stopped)
            SetBottomLine(item, "任务已停止；可重置后重新搜索", ColorWarning)
        Catch ex As Exception
            If item.Cancellation.IsCancellationRequested Then
                SetTaskState(item, SearchTaskState.Stopped)
                SetBottomLine(item, "任务已停止；可重置后重新搜索", ColorWarning)
            Else
                item.ErrorMessage = ex.Message
                SetTaskState(item, SearchTaskState.Failed)
                SetBottomLine(item, CompactMessage(ex.Message), ColorDanger)
            End If
        Finally
            ClearPendingProgress(item)
            If item.Runner IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(item.Runner.CurrentCommandLine) Then
                item.CommandLine = item.Runner.CurrentCommandLine
            End If
            item.Cancellation.Dispose()
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
                Case SearchTaskState.Pending
                    SetTaskState(item, SearchTaskState.Stopped)
                    SetBottomLine(item, "任务未启动即被停止；可重置后重新搜索", ColorWarning)
                    changed += 1
                Case SearchTaskState.Running, SearchTaskState.Paused
                    SetTaskState(item, SearchTaskState.Stopping)
                    item.Cancellation?.Cancel()
                    changed += 1
            End Select
        Next
        If changed > 0 Then UpdateStatus($"正在停止 {changed} 个任务…")
        If changed > 0 Then RefreshRows(targets)
        RefreshActionButtons()
    End Sub

    Private Sub PauseOrResumeTasks(sender As Object, e As EventArgs)
        Dim targets = GetOperationTargets(fallbackToActive:=True)
        Dim activeTargets = targets.Where(
            Function(item) item.State = SearchTaskState.Running OrElse
                           item.State = SearchTaskState.Paused).ToList()
        If activeTargets.Count = 0 Then
            UpdateStatus("请选择正在运行或已暂停的任务。")
            Return
        End If

        '只要可操作的选择中仍有运行项，本次点击就统一执行“暂停”；
        '只有所有可操作项均已暂停时才执行“恢复”。等待/结束项始终忽略。
        Dim shouldPause = activeTargets.Any(Function(item) item.State = SearchTaskState.Running)
        Dim changed = 0
        Dim lastError = String.Empty
        For Each item In activeTargets
            If item.Runner Is Nothing Then Continue For
            If shouldPause AndAlso item.State = SearchTaskState.Running Then
                Dim errorMessage As String = Nothing
                If item.Runner.TryPause(errorMessage) Then
                    SetTaskState(item, SearchTaskState.Paused)
                    changed += 1
                Else
                    lastError = errorMessage
                End If
            ElseIf Not shouldPause AndAlso item.State = SearchTaskState.Paused Then
                Dim errorMessage As String = Nothing
                If item.Runner.TryResume(errorMessage) Then
                    SetTaskState(item, SearchTaskState.Running)
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
        ElseIf lastError <> "" Then
            UpdateStatus("无法切换状态：" & CompactMessage(lastError))
        Else
            UpdateStatus("所选任务中没有可执行此操作的活动任务。")
        End If
        If changed > 0 Then RefreshRows(activeTargets)
        RefreshActionButtons()
    End Sub

    Private Sub RemoveTasks(sender As Object, e As EventArgs)
        Dim targets = GetSelectedQueueItems()
        If targets.Count = 0 Then
            UpdateStatus("请先选择要移除的任务。")
            Return
        End If
        If targets.Any(Function(item) item.State = SearchTaskState.Running OrElse
                                      item.State = SearchTaskState.Paused OrElse
                                      item.State = SearchTaskState.Stopping) Then
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
            If item.State = SearchTaskState.Running OrElse
               item.State = SearchTaskState.Paused OrElse
               item.State = SearchTaskState.Stopping Then Continue For

            item.Result = Nothing
            item.OutputPath = String.Empty
            item.ErrorMessage = String.Empty
            item.CommandLine = String.Empty
            item.Row.SubItems(2).Text = "—"
            item.Row.SubItems(3).Text = "—"
            item.Row.SubItems(4).Text = "—"
            SetTaskState(item, SearchTaskState.Pending)
            SetBottomLine(item, item.Path, ColorMuted)
            changed += 1
        Next

        If changed > 0 Then RefreshRows(targets)
        If changed > 0 Then
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
            _contextMenuTarget = TryCast(_fileList.Items(hitIndex).Tag, QueueFileItem)
            If Not _fileList.SelectedIndices.Contains(hitIndex) Then
                '右键未选中的项目时，让菜单明确作用于该项目；右键已有选择则保留多选。
                _fileList.SelectedIndex = hitIndex
            End If
        End If

        RebuildTaskContextMenu()
        If _taskContextMenu.Items.Count > 0 Then
            _taskContextMenu.Show(_fileList, e.X, e.Y)
        End If
    End Sub

    Private Sub RebuildTaskContextMenu()
        _taskContextMenu.Items.Clear()

        Dim selected = GetSelectedQueueItems()
        Dim targets = GetOperationTargets(fallbackToActive:=True)
        Dim activeTargets = targets.Where(
            Function(item) item.State = SearchTaskState.Running OrElse
                           item.State = SearchTaskState.Paused).ToList()

        If _contextMenuTarget IsNot Nothing Then
            AddTaskContextMenuItem("复制此任务的完整命令行", AddressOf CopyTaskCommandLine)
        End If

        Dim hasLifecycleAction = (Not _running AndAlso _items.Any(Function(item) item.State = SearchTaskState.Pending)) OrElse
                                 activeTargets.Count > 0 OrElse
                                 targets.Any(Function(item) item.State = SearchTaskState.Pending OrElse
                                                           item.State = SearchTaskState.Running OrElse
                                                           item.State = SearchTaskState.Paused)
        If hasLifecycleAction AndAlso _taskContextMenu.Items.Count > 0 Then AddTaskContextMenuSeparator()

        If Not _running AndAlso _items.Any(Function(item) item.State = SearchTaskState.Pending) Then
            AddTaskContextMenuItem("开始搜索", AddressOf StartQueue)
        End If

        If activeTargets.Any(Function(item) item.State = SearchTaskState.Running) Then
            AddTaskContextMenuItem("暂停", AddressOf PauseOrResumeTasks)
        ElseIf activeTargets.Count > 0 Then
            AddTaskContextMenuItem("恢复", AddressOf PauseOrResumeTasks)
        End If

        If targets.Any(Function(item) item.State = SearchTaskState.Pending OrElse
                                      item.State = SearchTaskState.Running OrElse
                                      item.State = SearchTaskState.Paused) Then
            AddTaskContextMenuItem("停止", AddressOf StopTasks, danger:=True)
        End If

        Dim canReset = selected.Any(
            Function(item) item.State <> SearchTaskState.Running AndAlso
                           item.State <> SearchTaskState.Paused AndAlso
                           item.State <> SearchTaskState.Stopping)
        Dim canRemove = selected.Count > 0 AndAlso
                        Not selected.Any(
                            Function(item) item.State = SearchTaskState.Running OrElse
                                           item.State = SearchTaskState.Paused OrElse
                                           item.State = SearchTaskState.Stopping)
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
                Dim settings = ReadSearchSettings()
                commandLine = Await AbAv1Runner.BuildCommandLineAsync(
                    profile,
                    item.Path,
                    settings,
                    _lifetimeCancellation.Token)
            End If

            CopyCommandLineToClipboard(
                commandLine,
                $"已复制 {Path.GetFileName(item.Path)} 的完整 ab-av1 命令行。")
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
        _taskContextMenu.Items.Add(
            New ModernContextMenu.ModernMenuItem With {
                .IsSeparator = True
            })
    End Sub

    Private Sub QueueSelectionChanged(sender As Object, e As EventArgs)
        RefreshActionButtons()
    End Sub

    Private Function GetSelectedQueueItems() As List(Of QueueFileItem)
        Dim result As New List(Of QueueFileItem)()
        For Each row In _fileList.SelectedItems
            Dim item = TryCast(row.Tag, QueueFileItem)
            If item IsNot Nothing AndAlso _items.Contains(item) Then result.Add(item)
        Next
        Return result
    End Function

    Private Function GetOperationTargets(fallbackToActive As Boolean) As List(Of QueueFileItem)
        Dim selected = GetSelectedQueueItems()
        If selected.Count = 0 AndAlso fallbackToActive AndAlso _activeItem IsNot Nothing Then selected.Add(_activeItem)
        Return selected
    End Function

    Private Function ReadSearchSettings() As SearchSettings
        Dim targetScore As Double
        Dim minCrf As Double
        Dim maxCrf As Double
        If Not TryParseNumber(_targetScore.Text, targetScore) Then Throw New FormatException("目标分数不是有效数字。")
        If Not TryParseNumber(_minCrf.Text, minCrf) Then Throw New FormatException("最小 CRF 不是有效数字。")
        If Not TryParseNumber(_maxCrf.Text, maxCrf) Then Throw New FormatException("最大 CRF 不是有效数字。")

        Dim sampleCount As Integer? = Nothing
        If Not String.IsNullOrWhiteSpace(_samples.Text) Then
            Dim parsed As Integer
            If Not Integer.TryParse(_samples.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, parsed) Then
                Throw New FormatException("采样数量不是有效整数。")
            End If
            sampleCount = parsed
        End If

        Dim settings As New SearchSettings With {
            .Metric = GetSelectedMetric(),
            .TargetScore = targetScore,
            .MinCrf = minCrf,
            .MaxCrf = maxCrf,
            .Samples = sampleCount,
            .SampleDuration = _sampleDuration.Text.Trim(),
            .Thorough = _thorough.Checked,
            .VmafModel = _vmafModel.Text.Trim()
        }
        settings.Validate()
        Return settings
    End Function

    Private Sub SetRunning(value As Boolean)
        _running = value
        _startButton.Enabled = Not value
        _presetPath.Enabled = Not value
        _outputDirectory.Enabled = Not value
        _scoreMetric.Enabled = Not value
        _targetScore.Enabled = Not value
        _minCrf.Enabled = Not value
        _maxCrf.Enabled = Not value
        _samples.Enabled = Not value
        _sampleDuration.Enabled = Not value
        _thorough.Enabled = Not value
        Dim useVmaf = GetSelectedMetric() = QualityMetric.Vmaf
        _vmafModel.Enabled = Not value AndAlso useVmaf
        _refreshModelsButton.Enabled = Not value AndAlso useVmaf AndAlso Not _scanningModels
        _browseModelButton.Enabled = Not value AndAlso useVmaf
        '添加媒体和拖放在运行期间保持可用，新项目进入等待队列。
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
                            New List(Of QueueFileItem)(),
                            New List(Of QueueFileItem) From {_activeItem}))
        _stopButton.Enabled = targets.Any(Function(item) item.State = SearchTaskState.Pending OrElse
                                                         item.State = SearchTaskState.Running OrElse
                                                         item.State = SearchTaskState.Paused)
        Dim activeTargets = targets.Where(
            Function(item) item.State = SearchTaskState.Running OrElse
                           item.State = SearchTaskState.Paused).ToList()
        _pauseResumeButton.Enabled = activeTargets.Count > 0
        If activeTargets.Any(Function(item) item.State = SearchTaskState.Running) Then
            _pauseResumeButton.Text = "暂停"
        ElseIf activeTargets.Count > 0 Then
            _pauseResumeButton.Text = "恢复"
        Else
            _pauseResumeButton.Text = "暂停 / 恢复"
        End If
        _removeButton.Enabled = selected.Count > 0 AndAlso
                                Not selected.Any(Function(item) item.State = SearchTaskState.Running OrElse
                                                               item.State = SearchTaskState.Paused OrElse
                                                               item.State = SearchTaskState.Stopping)
        _resetButton.Enabled = selected.Any(Function(item) item.State <> SearchTaskState.Running AndAlso
                                                           item.State <> SearchTaskState.Paused AndAlso
                                                           item.State <> SearchTaskState.Stopping)
    End Sub

    Private Sub SetTaskState(item As QueueFileItem, state As SearchTaskState)
        item.State = state
        Dim text As String
        Dim color As Color
        Select Case state
            Case SearchTaskState.Pending
                text = "等待"
                color = ColorMuted
            Case SearchTaskState.Running
                text = "搜索中"
                color = ColorAccent
            Case SearchTaskState.Paused
                text = "已暂停"
                color = ColorWarning
            Case SearchTaskState.Stopping
                text = "正在停止"
                color = ColorWarning
            Case SearchTaskState.Enqueued
                text = "已入队"
                color = ColorSuccess
            Case SearchTaskState.Failed
                text = "失败"
                color = ColorDanger
            Case SearchTaskState.Stopped
                text = "已停止"
                color = ColorWarning
            Case Else
                text = state.ToString()
                color = ColorMuted
        End Select
        item.Row.SubItems(1).Text = text
        item.Row.SubItems(1).ForeColor = color
    End Sub

    Private Sub SetBottomLine(item As QueueFileItem, text As String, color As Color)
        item.Row.BottomLines.Clear()
        item.Row.BottomLines.Add(New UltraDetailListView.TextLine(text, _detailFont, color))
    End Sub

    Private Sub QueueProgressRender(item As QueueFileItem, update As SearchProgress)
        If IsDisposed OrElse item Is Nothing OrElse Not _items.Contains(item) Then Return
        If item.State <> SearchTaskState.Running AndAlso item.State <> SearchTaskState.Paused Then Return

        _pendingProgressItem = item
        _pendingProgressStatus = $"{Path.GetFileName(item.Path)} · {CompactMessage(update.Message)}"
        If update.TestedCrf.HasValue Then
            _pendingProgressCrf = SearchSettings.FormatNumber(update.TestedCrf.Value)
        End If
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
        Dim crfText = _pendingProgressCrf
        Dim scoreText = _pendingProgressScore
        _pendingProgressItem = Nothing
        _pendingProgressStatus = Nothing
        _pendingProgressCrf = Nothing
        _pendingProgressScore = Nothing

        If statusText IsNot Nothing Then UpdateStatus(statusText)
        If item Is Nothing OrElse Not _items.Contains(item) Then Return
        If item.State <> SearchTaskState.Running AndAlso item.State <> SearchTaskState.Paused Then Return

        Dim changed = False
        If crfText IsNot Nothing AndAlso item.Row.SubItems(2).Text <> crfText Then
            item.Row.SubItems(2).Text = crfText
            changed = True
        End If
        If scoreText IsNot Nothing AndAlso item.Row.SubItems(3).Text <> scoreText Then
            item.Row.SubItems(3).Text = scoreText
            changed = True
        End If
        If changed Then RefreshRows({item})
    End Sub

    Private Sub ClearPendingProgress(item As QueueFileItem)
        If Not Object.ReferenceEquals(_pendingProgressItem, item) Then Return
        If Not IsDisposed Then _progressRenderTimer.Stop()
        _pendingProgressItem = Nothing
        _pendingProgressStatus = Nothing
        _pendingProgressCrf = Nothing
        _pendingProgressScore = Nothing
    End Sub

    Private Sub RefreshRows(items As IEnumerable(Of QueueFileItem))
        If IsDisposed OrElse items Is Nothing Then Return
        Dim targets = items.Where(
            Function(item) item IsNot Nothing AndAlso _items.Contains(item)).Distinct().ToList()
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
        If String.Equals(_status.Text, text, StringComparison.Ordinal) Then Return
        _status.Text = text
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
        list.Columns.Add(New UltraDetailListView.ListColumn("文件", 520))
        list.Columns.Add(New UltraDetailListView.ListColumn("状态", 110))
        list.Columns.Add(New UltraDetailListView.ListColumn("CRF", 80))
        list.Columns.Add(New UltraDetailListView.ListColumn("分数", 130))
        list.Columns.Add(New UltraDetailListView.ListColumn("预测大小", 130))
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

    Private Shared Function CreateFieldLabel(text As String) As Label
        Dim label = CreateLabel(text, ColorMuted, 9.0F)
        label.Dock = DockStyle.Fill
        label.TextAlign = ContentAlignment.MiddleLeft
        label.Margin = New Padding(2, 0, 2, 0)
        Return label
    End Function

    Private Shared Function CreateLabel(text As String,
                                        color As Color,
                                        size As Single,
                                        Optional style As FontStyle = FontStyle.Regular) As Label
        Return New Label With {
            .Text = text,
            .ForeColor = color,
            .BackColor = Color.Transparent,
            .Font = New Font("Microsoft YaHei UI", size, style),
            .UseMnemonic = False
        }
    End Function

    Private Enum SearchTaskState
        Pending
        Running
        Paused
        Stopping
        Enqueued
        Failed
        Stopped
    End Enum

    Private NotInheritable Class QueueFileItem
        Public Sub New(path As String, row As UltraDetailListView.ListItem)
            Me.Path = path
            Me.Row = row
        End Sub

        Public ReadOnly Property Path As String
        Public ReadOnly Property Row As UltraDetailListView.ListItem
        Public Property State As SearchTaskState = SearchTaskState.Pending
        Public Property Runner As AbAv1Runner
        Public Property Cancellation As CancellationTokenSource
        Public Property Result As SearchResult
        Public Property OutputPath As String = String.Empty
        Public Property ErrorMessage As String = String.Empty
        Public Property CommandLine As String = String.Empty
    End Class

End Class
