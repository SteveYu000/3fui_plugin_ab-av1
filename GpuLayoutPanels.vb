Imports System.Drawing
Imports System.Linq
Imports System.ComponentModel
Imports System.Windows.Forms
Imports LakeUI

Friend Module GpuBackgroundBinding
    ''' <summary>
    ''' Makes controls with LakeUI's public BackgroundSource property sample a
    ''' stable outer GPU surface directly. Automatic nearest-ancestor sampling
    ''' deliberately does not register dependency invalidations in LakeUI 5.5;
    ''' an explicit source does, so a temporarily unavailable backdrop cannot
    ''' leave a child swap chain presenting its cleared black frame indefinitely.
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
''' A small grid layout container backed by a LakeUI GPU surface.
'''
''' LakeUI 5 renders every LakeUI control through its own presentation surface.
''' A transparent WinForms TableLayoutPanel between those surfaces asks GDI to
''' repaint a DirectX parent and can therefore display stale/foreign pixels.
''' Keeping layout containers inside the LakeUI control tree avoids that mixed
''' GDI/DirectX background path while retaining the responsive grid layout.
''' </summary>
Friend NotInheritable Class GpuGridPanel
    Inherits JustEmptyControl

    Friend NotInheritable Class GridControlCollection
        Inherits Control.ControlCollection

        Private ReadOnly _owner As GpuGridPanel

        Public Sub New(owner As GpuGridPanel)
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
    ''' Keeps child HWND/GPU surfaces at their last valid size while this grid is
    ''' collapsed to zero width or height. This is useful for optional rows:
    ''' LakeUI 5.5 releases a presenter's resources on Visible=False, and resizing
    ''' every child to zero forces all swap chains to be rebuilt on expansion.
    ''' The zero-sized parent still clips the retained children completely.
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
        'The native WinForms layout engine runs before this grid's OnLayout. If a
        'child keeps Dock=Fill, WinForms first expands it to the whole container
        'and this grid immediately moves it back into its cell. On LakeUI 5.5
        'each of those two bounds changes recreates/invalidates a GPU surface and
        'queues another layout, causing thousands of frames during a tab switch.
        'Retain the requested docking semantics here and keep the actual child
        'undocked so only this grid is allowed to assign its bounds.
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
        'Keep the requested docking style while a control is temporarily
        'detached. The compact search layout clears and re-adds the same field
        'controls; their actual Dock is intentionally None at that point.
        MyBase.OnControlRemoved(e)
    End Sub

    Protected Overrides Sub OnLayout(levent As LayoutEventArgs)
        'LakeUI may perform layout from its base constructor, before this
        'derived type's field initializers have run.
        If _cells Is Nothing OrElse _dockStyles Is Nothing OrElse
           _columnStyles Is Nothing OrElse _rowStyles Is Nothing Then
            MyBase.OnLayout(levent)
            Return
        End If
        'Bounds changes can synchronously re-enter layout. Do not let the native
        'dock layout overwrite a partially applied grid during that re-entry.
        If _layingOut OrElse IsDisposed Then Return

        _layingOut = True
        Try
            MyBase.OnLayout(levent)
            Dim content = New Rectangle(
                Padding.Left,
                Padding.Top,
                Math.Max(0, ClientSize.Width - Padding.Horizontal),
                Math.Max(0, ClientSize.Height - Padding.Vertical))
            'Do not resize retained child HWNDs to 0x0. The parent clips them while
            'collapsed, and their already-presented transparent surfaces can be
            'shown immediately when the row is expanded again.
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
        'A proposed size is a constraint, not a minimum. In particular, summing
        'the preferred widths of percentage columns creates an AutoSize feedback
        'loop (1470 -> 5920 -> ...). Keep the proposed width and independently
        'measure the row height required by the content.
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
''' Prevents LakeUI 5.5's non-editable combo box from retaining a caret scroll
''' offset calculated at a narrow intermediate construction size. The base
''' control resets that offset on resize only for non-left text alignment, so
''' briefly use centered alignment while it recalculates and restore the visual
''' left alignment before the queued GPU paint runs.
''' </summary>
Friend NotInheritable Class StableModernComboBox
    Inherits ModernComboBox

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
''' Horizontal flow container that remains part of the LakeUI GPU control tree.
''' </summary>
Friend NotInheritable Class GpuFlowPanel
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
