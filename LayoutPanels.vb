Imports System.Drawing
Imports System.Linq
Imports System.ComponentModel
Imports System.Windows.Forms
Imports LakeUI

Friend Module LayoutBackgroundBinding
    ''' <summary>
    ''' 通过 LakeUI 的公开 BackgroundSource 属性，将控件绑定到稳定的外层底板。
    ''' LakeUI 5.5 自动寻找最近祖先作为背景时，不会注册背景失效依赖；
    ''' 显式绑定可让控件随背景变化及时重绘，避免背景暂时不可用后长期残留黑块。
    ''' </summary>
    Public Sub BindImmediateChildren(container As Control, source As Control)
        If container Is Nothing OrElse source Is Nothing Then Return

        For Each child As Control In container.Controls
            If child Is Nothing OrElse child.IsDisposed OrElse ReferenceEquals(child, source) Then Continue For
            Dim backgroundSourceProperty = child.GetType().GetProperty("BackgroundSource")
            If backgroundSourceProperty Is Nothing OrElse
               Not backgroundSourceProperty.CanWrite OrElse
               Not GetType(Control).IsAssignableFrom(backgroundSourceProperty.PropertyType) Then Continue For
            backgroundSourceProperty.SetValue(child, source)
        Next
    End Sub
End Module

''' <summary>
''' 基于 LakeUI 控件的轻量网格布局容器，绘制仍由 LakeUI 负责。
'''
''' LakeUI 5 为各控件维护独立的绘制表面。若在控件之间插入透明的
''' WinForms TableLayoutPanel，会通过 GDI 请求重绘 DirectX 父控件，
''' 可能显示过期或来自其他控件的画面。使用 LakeUI 控件承载布局，
''' 可以保持行列布局能力，同时避免混用两种背景绘制路径。
''' </summary>
Friend NotInheritable Class LayoutGridPanel
    Inherits JustEmptyControl

    Friend NotInheritable Class GridControlCollection
        Inherits Control.ControlCollection

        Private ReadOnly _owner As LayoutGridPanel

        Public Sub New(owner As LayoutGridPanel)
            MyBase.New(owner)
            _owner = owner
        End Sub

        Public Overloads Sub Add(value As Control, column As Integer, row As Integer)
            _owner.AddControl(value, column, row)
        End Sub
    End Class

    Private NotInheritable Class GridCell
        Public Property Column As Integer
        Public Property Row As Integer
        Public Property ColumnSpan As Integer = 1
        Public Property RowSpan As Integer = 1
    End Class

    Private ReadOnly _cells As New Dictionary(Of Control, GridCell)()
    Private ReadOnly _dockStyles As New Dictionary(Of Control, DockStyle)()
    Private ReadOnly _columnStyles As New List(Of ColumnStyle)()
    Private ReadOnly _rowStyles As New List(Of RowStyle)()
    Private _columnCount As Integer = 1
    Private _rowCount As Integer = 1
    Private _layingOut As Boolean
    Private _preserveChildBoundsWhenCollapsed As Boolean

    Public Sub New()
        BackColor = Color.Transparent
        Margin = Padding.Empty
        Padding = Padding.Empty
    End Sub

    Protected Overrides Function CreateControlsInstance() As Control.ControlCollection
        Return New GridControlCollection(Me)
    End Function

    Public Shadows ReadOnly Property Controls As GridControlCollection
        Get
            Return DirectCast(MyBase.Controls, GridControlCollection)
        End Get
    End Property

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property ColumnCount As Integer
        Get
            Return _columnCount
        End Get
        Set(value As Integer)
            Dim normalized = Math.Max(1, value)
            If _columnCount = normalized Then Return
            _columnCount = normalized
            PerformLayout()
        End Set
    End Property

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property RowCount As Integer
        Get
            Return _rowCount
        End Get
        Set(value As Integer)
            Dim normalized = Math.Max(1, value)
            If _rowCount = normalized Then Return
            _rowCount = normalized
            PerformLayout()
        End Set
    End Property

    Public ReadOnly Property ColumnStyles As List(Of ColumnStyle)
        Get
            Return _columnStyles
        End Get
    End Property

    Public ReadOnly Property RowStyles As List(Of RowStyle)
        Get
            Return _rowStyles
        End Get
    End Property

    ''' <summary>
    ''' 网格宽度或高度折叠为零时，保留子控件窗口和绘制表面的最后有效尺寸。
    ''' 此设置用于可隐藏的参数行：LakeUI 5.5 在 Visible=False 时释放绘制资源，
    ''' 将子控件尺寸设为零也会导致再次展开时重建交换链。
    ''' 折叠后的父容器仍会完整裁剪子控件，使其不显示。
    ''' </summary>
    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property PreserveChildBoundsWhenCollapsed As Boolean
        Get
            Return _preserveChildBoundsWhenCollapsed
        End Get
        Set(value As Boolean)
            _preserveChildBoundsWhenCollapsed = value
        End Set
    End Property

    Public Sub AddControl(control As Control, column As Integer, row As Integer)
        If control Is Nothing Then Throw New ArgumentNullException(NameOf(control))
        'WinForms 原生布局先于本容器的 OnLayout 执行。若子控件保持 Dock=Fill，
        '原生布局会先将其铺满整个容器，本容器又立即将其调整回单元格。
        'LakeUI 5.5 会随这些尺寸变化重建或使绘制表面失效，并再次触发布局，
        '造成页面切换时持续重绘和闪烁。因此记录原来的停靠方式，
        '将实际 Dock 设为 None，只由本容器计算和设置子控件的位置与尺寸。
        If Not _dockStyles.ContainsKey(control) Then _dockStyles(control) = control.Dock
        control.Dock = DockStyle.None
        _cells(control) = New GridCell With {
            .Column = Math.Max(0, column),
            .Row = Math.Max(0, row)
        }
        Controls.Add(control)
    End Sub

    Public Sub SetColumnSpan(control As Control, span As Integer)
        If control Is Nothing Then Return
        Dim cell = GetOrCreateCell(control)
        cell.ColumnSpan = Math.Max(1, span)
        PerformLayout()
    End Sub

    Public Sub SetRowSpan(control As Control, span As Integer)
        If control Is Nothing Then Return
        Dim cell = GetOrCreateCell(control)
        cell.RowSpan = Math.Max(1, span)
        PerformLayout()
    End Sub

    Protected Overrides Sub OnControlRemoved(e As ControlEventArgs)
        If _cells IsNot Nothing AndAlso e.Control IsNot Nothing Then _cells.Remove(e.Control)
        '控件暂时移出容器时，保留记录的停靠方式。紧凑布局会清空并重新添加
        '同一批参数控件，此时其实际 Dock 已设为 None，不能用它覆盖原有记录。
        MyBase.OnControlRemoved(e)
    End Sub

    Protected Overrides Sub OnLayout(levent As LayoutEventArgs)
        'LakeUI 基类构造函数可能在本类字段初始化前触发布局。
        If _cells Is Nothing OrElse _dockStyles Is Nothing OrElse
           _columnStyles Is Nothing OrElse _rowStyles Is Nothing Then
            MyBase.OnLayout(levent)
            Return
        End If
        '位置或尺寸变化可能同步触发布局重入，避免原生停靠布局覆盖尚未完成的网格布局。
        If _layingOut OrElse IsDisposed Then Return

        _layingOut = True
        Try
            MyBase.OnLayout(levent)
            Dim content = New Rectangle(
                Padding.Left,
                Padding.Top,
                Math.Max(0, ClientSize.Width - Padding.Horizontal),
                Math.Max(0, ClientSize.Height - Padding.Vertical))
            '折叠时不将保留的子控件窗口缩为 0×0；父容器会裁剪其显示区域，
            '再次展开时可以直接显示已经绘制好的透明背景。
            If _preserveChildBoundsWhenCollapsed AndAlso
               (content.Width <= 0 OrElse content.Height <= 0) Then Return
            Dim columnWidths = CalculateTracks(vertical:=False, _columnCount, _columnStyles, content.Width)
            Dim rowHeights = CalculateTracks(vertical:=True, _rowCount, _rowStyles, content.Height)
            Dim columnOffsets = BuildOffsets(content.Left, columnWidths)
            Dim rowOffsets = BuildOffsets(content.Top, rowHeights)

            For Each control As Control In Controls
                If control Is Nothing OrElse control.IsDisposed OrElse Not control.Visible Then Continue For
                Dim cell = GetOrCreateCell(control)
                Dim column = Math.Min(Math.Max(0, cell.Column), columnWidths.Length - 1)
                Dim row = Math.Min(Math.Max(0, cell.Row), rowHeights.Length - 1)
                Dim columnSpan = Math.Min(Math.Max(1, cell.ColumnSpan), columnWidths.Length - column)
                Dim rowSpan = Math.Min(Math.Max(1, cell.RowSpan), rowHeights.Length - row)
                Dim bounds = New Rectangle(
                    columnOffsets(column),
                    rowOffsets(row),
                    SumTracks(columnWidths, column, columnSpan),
                    SumTracks(rowHeights, row, rowSpan))
                Dim requestedDock = DockStyle.None
                If Not _dockStyles.TryGetValue(control, requestedDock) Then requestedDock = control.Dock
                ApplyCellBounds(control, bounds, requestedDock)
            Next
        Finally
            _layingOut = False
        End Try
    End Sub

    Public Overrides Function GetPreferredSize(proposedSize As Size) As Size
        If _cells Is Nothing OrElse _columnStyles Is Nothing OrElse _rowStyles Is Nothing Then
            Return MyBase.GetPreferredSize(proposedSize)
        End If
        Dim proposedContentWidth = Math.Max(0, proposedSize.Width - Padding.Horizontal)
        '建议尺寸是布局约束，不是最小尺寸。累加百分比列的首选宽度会导致
        'AutoSize 反馈循环，使宽度不断增大。保留传入的宽度约束，
        '单独测量内容所需的行高。
        Dim preferredWidth = If(proposedContentWidth > 0,
                                proposedContentWidth,
                                CalculatePreferredAxis(vertical:=False, _columnCount, _columnStyles))
        Dim preferredHeight = CalculatePreferredAxis(vertical:=True, _rowCount, _rowStyles)
        Return New Size(
            Math.Max(MinimumSize.Width, preferredWidth + Padding.Horizontal),
            Math.Max(MinimumSize.Height, preferredHeight + Padding.Vertical))
    End Function

    Private Function GetOrCreateCell(control As Control) As GridCell
        Dim cell As GridCell = Nothing
        If Not _cells.TryGetValue(control, cell) Then
            cell = New GridCell()
            _cells(control) = cell
        End If
        Return cell
    End Function

    Private Function CalculateTracks(Of TStyle As TableLayoutStyle)(vertical As Boolean,
                                                                    count As Integer,
                                                                    styles As List(Of TStyle),
                                                                    available As Integer) As Integer()
        Dim result(Math.Max(1, count) - 1) As Integer
        Dim percentTotal As Single
        Dim fixedTotal As Integer

        For index = 0 To result.Length - 1
            Dim sizeType = GetSizeType(styles, index)
            Select Case sizeType
                Case SizeType.Absolute
                    result(index) = Math.Max(0, CInt(Math.Round(GetStyleSize(styles, index))))
                    fixedTotal += result(index)
                Case SizeType.AutoSize
                    result(index) = MeasureAutoTrack(index, vertical)
                    fixedTotal += result(index)
                Case SizeType.Percent
                    percentTotal += Math.Max(0.0F, GetStyleSize(styles, index))
            End Select
        Next

        Dim remaining = Math.Max(0, available - fixedTotal)
        If percentTotal <= 0.0F Then
            Dim unassigned = Enumerable.Range(0, result.Length).
                Where(Function(index) GetSizeType(styles, index) = SizeType.Percent).
                ToArray()
            If unassigned.Length > 0 Then
                Dim used As Integer
                For position = 0 To unassigned.Length - 1
                    Dim length = If(position = unassigned.Length - 1,
                                    remaining - used,
                                    CInt(Math.Floor(CDbl(remaining) / unassigned.Length)))
                    result(unassigned(position)) = Math.Max(0, length)
                    used += length
                Next
            End If
            Return result
        End If

        Dim allocated As Integer
        Dim lastPercentIndex = -1
        For index = 0 To result.Length - 1
            If GetSizeType(styles, index) <> SizeType.Percent Then Continue For
            lastPercentIndex = index
            Dim share = CInt(Math.Floor(remaining * (Math.Max(0.0F, GetStyleSize(styles, index)) / percentTotal)))
            result(index) = Math.Max(0, share)
            allocated += result(index)
        Next
        If lastPercentIndex >= 0 Then result(lastPercentIndex) += Math.Max(0, remaining - allocated)
        Return result
    End Function

    Private Function CalculatePreferredAxis(Of TStyle As TableLayoutStyle)(vertical As Boolean,
                                                                            count As Integer,
                                                                            styles As List(Of TStyle)) As Integer
        Dim total As Integer
        For index = 0 To Math.Max(1, count) - 1
            Select Case GetSizeType(styles, index)
                Case SizeType.Absolute
                    total += Math.Max(0, CInt(Math.Round(GetStyleSize(styles, index))))
                Case SizeType.AutoSize
                    total += MeasureAutoTrack(index, vertical)
                Case SizeType.Percent
                    total += MeasurePercentTrack(index, vertical)
            End Select
        Next
        Return total
    End Function

    Private Function MeasureAutoTrack(index As Integer, vertical As Boolean) As Integer
        Dim maximum As Integer
        For Each pair In _cells
            Dim control = pair.Key
            Dim cell = pair.Value
            If control Is Nothing OrElse control.IsDisposed OrElse Not control.Visible OrElse
               Not ReferenceEquals(control.Parent, Me) Then Continue For
            If vertical Then
                If cell.Row <> index OrElse cell.RowSpan <> 1 Then Continue For
            Else
                If cell.Column <> index OrElse cell.ColumnSpan <> 1 Then Continue For
            End If
            Dim preferred = control.GetPreferredSize(New Size(Math.Max(0, ClientSize.Width), Math.Max(0, ClientSize.Height)))
            Dim measured = If(vertical,
                              preferred.Height + control.Margin.Vertical,
                              preferred.Width + control.Margin.Horizontal)
            maximum = Math.Max(maximum, measured)
        Next
        Return maximum
    End Function

    Private Function MeasurePercentTrack(index As Integer, vertical As Boolean) As Integer
        Dim maximum As Integer
        For Each pair In _cells
            Dim control = pair.Key
            Dim cell = pair.Value
            If control Is Nothing OrElse control.IsDisposed OrElse Not control.Visible OrElse
               Not ReferenceEquals(control.Parent, Me) Then Continue For
            If vertical Then
                If cell.Row <> index OrElse cell.RowSpan <> 1 Then Continue For
            Else
                If cell.Column <> index OrElse cell.ColumnSpan <> 1 Then Continue For
            End If
            Dim preferred = control.GetPreferredSize(Size.Empty)
            maximum = Math.Max(maximum,
                               If(vertical,
                                  preferred.Height + control.Margin.Vertical,
                                  preferred.Width + control.Margin.Horizontal))
        Next
        Return maximum
    End Function

    Private Shared Function GetSizeType(Of TStyle As TableLayoutStyle)(styles As List(Of TStyle), index As Integer) As SizeType
        If index < styles.Count Then Return styles(index).SizeType
        Return SizeType.Percent
    End Function

    Private Shared Function GetStyleSize(Of TStyle As TableLayoutStyle)(styles As List(Of TStyle), index As Integer) As Single
        If index >= styles.Count Then Return 100.0F
        Dim column = TryCast(styles(index), ColumnStyle)
        If column IsNot Nothing Then Return column.Width
        Dim row = TryCast(styles(index), RowStyle)
        If row IsNot Nothing Then Return row.Height
        Return 0.0F
    End Function

    Private Shared Function BuildOffsets(origin As Integer, tracks As Integer()) As Integer()
        Dim offsets(tracks.Length - 1) As Integer
        Dim current = origin
        For index = 0 To tracks.Length - 1
            offsets(index) = current
            current += tracks(index)
        Next
        Return offsets
    End Function

    Private Shared Function SumTracks(tracks As Integer(), start As Integer, count As Integer) As Integer
        Dim total As Integer
        For index = start To start + count - 1
            total += tracks(index)
        Next
        Return total
    End Function

    Private Shared Sub ApplyCellBounds(control As Control,
                                       cellBounds As Rectangle,
                                       requestedDock As DockStyle)
        Dim margin = control.Margin
        Dim available = New Rectangle(
            cellBounds.Left + margin.Left,
            cellBounds.Top + margin.Top,
            Math.Max(0, cellBounds.Width - margin.Horizontal),
            Math.Max(0, cellBounds.Height - margin.Vertical))

        Dim bounds As Rectangle
        Select Case requestedDock
            Case DockStyle.Fill
                bounds = available
            Case DockStyle.Top
                bounds = New Rectangle(available.Left, available.Top, available.Width, Math.Min(control.Height, available.Height))
            Case DockStyle.Bottom
                Dim height = Math.Min(control.Height, available.Height)
                bounds = New Rectangle(available.Left, available.Bottom - height, available.Width, height)
            Case DockStyle.Left
                bounds = New Rectangle(available.Left, available.Top, Math.Min(control.Width, available.Width), available.Height)
            Case DockStyle.Right
                Dim width = Math.Min(control.Width, available.Width)
                bounds = New Rectangle(available.Right - width, available.Top, width, available.Height)
            Case Else
                Dim width = Math.Min(control.Width, available.Width)
                Dim height = Math.Min(control.Height, available.Height)
                If (control.Anchor And (AnchorStyles.Left Or AnchorStyles.Right)) = (AnchorStyles.Left Or AnchorStyles.Right) Then width = available.Width
                If (control.Anchor And (AnchorStyles.Top Or AnchorStyles.Bottom)) = (AnchorStyles.Top Or AnchorStyles.Bottom) Then height = available.Height
                bounds = New Rectangle(available.Left, available.Top, Math.Max(0, width), Math.Max(0, height))
        End Select
        If control.Bounds <> bounds Then control.Bounds = bounds
    End Sub
End Class

''' <summary>
''' 修正 LakeUI 5.5 非编辑下拉框在初始化尺寸过窄时残留文字滚动偏移的问题。
''' 基类仅在非左对齐状态下随尺寸变化重置该偏移，因此重新计算时暂用居中对齐，
''' 再在后续绘制前恢复左对齐，避免文字开头被裁掉。
''' </summary>
Friend NotInheritable Class StableModernComboBox
    Inherits ModernComboBox

    Public Sub New()
        '使用 LakeUI 公开浮层接口，保持与 3FUI 原生下拉列表一致的样式。
        '自动采样宿主背景，并以覆盖浮层显示半透明毛玻璃列表。
        DropDownMode = DropDownDisplayMode.Overlay
        DropDownBackdropMode = PopupBackdropMode.Auto
        DropDownBackdropBlurRadius = 30
        DropDownBackdropBlurPasses = 2
        DropDownPadding = New Padding(10)
        DropDownHoverColor = Color.FromArgb(20, 220, 220, 220)
        DropDownSelectedColor = Color.FromArgb(40, 220, 220, 220)
        DropDownSelectedForeColor = Color.White
    End Sub

    Protected Overrides Sub OnSizeChanged(e As EventArgs)
        If Not Editable AndAlso
           TextAlign = ModernComboBox.TextAlignMode.Left AndAlso
           Not String.IsNullOrEmpty(Text) Then
            TextAlign = ModernComboBox.TextAlignMode.Center
            Try
                MyBase.OnSizeChanged(e)
            Finally
                TextAlign = ModernComboBox.TextAlignMode.Left
            End Try
            Return
        End If

        MyBase.OnSizeChanged(e)
    End Sub
End Class

''' <summary>
''' 基于 LakeUI ModernPanel 的横向流式布局容器，绘制仍由 LakeUI 负责。
''' </summary>
Friend NotInheritable Class LayoutFlowPanel
    Inherits ModernPanel

    Public Sub New()
        BackColor = Color.Transparent
        BackColor1 = Color.Transparent
        BorderSize = 0
        BorderRadius = 0
        Margin = Padding.Empty
        Padding = Padding.Empty
        LayoutMode = ModernPanel.LayoutModeEnum.Flow
        FlowDirection = ModernPanel.FlowDirectionEnum.LeftToRight
        WrapContents = True
    End Sub
End Class
