Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Drawing.Imaging
Imports System.Windows.Forms
Imports LakeUI

''' <summary>
''' Adds a shared capsule track through LakeUI's public tab-strip image API.
''' The native tab control still handles selection, keyboard navigation and
''' bound-page GPU refreshes. Only the small decorative image is generated here.
''' </summary>
Friend NotInheritable Class CapsulePageTabs
    Inherits ModernTabControl

    Private _stripImage As Bitmap
    Private _trackBounds As RectangleF
    Private _selectedBounds As RectangleF

    Protected Overrides Sub OnSizeChanged(e As EventArgs)
        MyBase.OnSizeChanged(e)
        UpdateCapsuleBackground()
    End Sub

    Protected Overrides Sub OnFontChanged(e As EventArgs)
        MyBase.OnFontChanged(e)
        UpdateCapsuleBackground()
    End Sub

    Protected Overrides Sub OnDpiChangedAfterParent(e As EventArgs)
        MyBase.OnDpiChangedAfterParent(e)
        UpdateCapsuleBackground()
    End Sub

    Protected Overrides Sub OnHandleCreated(e As EventArgs)
        MyBase.OnHandleCreated(e)
        UpdateCapsuleBackground()
    End Sub

    Public Sub UpdateCapsuleBackground()
        'Base constructors can raise size/font events before Items is initialized.
        If IsDisposed OrElse Items Is Nothing OrElse Items.Count = 0 OrElse ClientSize.Width <= 0 Then Return

        Dim scale = DeviceDpi / 96.0F
        Dim imageSize As New Size(ClientSize.Width, CInt(TabStripHeight * scale))
        If imageSize.Height <= 0 Then Return

        Dim itemHeight = imageSize.Height -
                         CInt(TabStripPadding.Top * scale) - CInt(TabStripPadding.Bottom * scale)
        If itemHeight <= 0 Then Return

        Dim itemWidths(Items.Count - 1) As Single
        Dim totalWidth = TabItemSpacing * scale * (Items.Count - 1)
        For index = 0 To Items.Count - 1
            Dim item = Items(index)
            Dim tabFont = If(item.TabFont, Font)
            Dim textWidth = TextRenderer.MeasureText(item.Text, tabFont,
                                                    New Size(Integer.MaxValue, Integer.MaxValue),
                                                    TextFormatFlags.NoPadding).Width
            itemWidths(index) = Math.Max(TabItemMinWidth * scale, textWidth + TabItemTextPadding * scale * 2)
            totalWidth += itemWidths(index)
        Next

        'Use the same available strip width as the native tab layout, so the
        'track and clickable tabs stay centered together after window resizes.
        Dim leftPadding = CInt(TabStripPadding.Left * scale)
        Dim rightPadding = CInt(TabStripPadding.Right * scale)
        Dim availableWidth = imageSize.Width - leftPadding - rightPadding
        Dim alignOffset As Single = 0
        If totalWidth < availableWidth Then
            Select Case TabAlignment
                Case TabAlignmentEnum.Center
                    alignOffset = (availableWidth - totalWidth) / 2.0F
                Case TabAlignmentEnum.Right
                    alignOffset = availableWidth - totalWidth
            End Select
        End If
        Dim x = leftPadding + alignOffset
        Dim y = CSng(CInt(TabStripPadding.Top * scale))
        Dim firstX = x
        Dim selectedBounds = RectangleF.Empty
        For index = 0 To Items.Count - 1
            If index = SelectedIndex Then selectedBounds = New RectangleF(x, y, itemWidths(index), itemHeight)
            x += itemWidths(index) + TabItemSpacing * scale
        Next
        'The selected pill is slightly taller than its track, like the reference.
        Dim trackBounds As New RectangleF(firstX, y + scale, totalWidth, itemHeight - scale * 2)

        'Height-only page resizes need no new texture. A selection/font/DPI change
        'only replaces this strip image once; hover animation remains native.
        If _stripImage IsNot Nothing AndAlso _stripImage.Size = imageSize AndAlso
           _trackBounds = trackBounds AndAlso _selectedBounds = selectedBounds Then Return

        Dim nextImage As New Bitmap(imageSize.Width, imageSize.Height, PixelFormat.Format32bppPArgb)
        Using drawing = Graphics.FromImage(nextImage)
            drawing.Clear(Color.Transparent)
            drawing.SmoothingMode = SmoothingMode.AntiAlias
            Using trackPath = CreateCapsulePath(trackBounds),
                  trackBrush As New SolidBrush(Color.FromArgb(176, 34, 36, 40))
                drawing.FillPath(trackBrush, trackPath)
            End Using

            If Not selectedBounds.IsEmpty Then
                Dim shadowBounds = selectedBounds
                shadowBounds.Offset(0, scale)
                shadowBounds.Inflate(scale * 0.5F, scale * 0.5F)
                Using shadowPath = CreateCapsulePath(shadowBounds),
                      shadowBrush As New SolidBrush(Color.FromArgb(18, 0, 0, 0))
                    drawing.FillPath(shadowBrush, shadowPath)
                End Using
                Dim outlineBounds = selectedBounds
                outlineBounds.Inflate(scale * 0.5F, scale * 0.5F)
                Using outlinePath = CreateCapsulePath(outlineBounds),
                      outlinePen As New Pen(Color.FromArgb(22, 255, 255, 255), scale)
                    drawing.DrawPath(outlinePen, outlinePath)
                End Using
            End If
        End Using

        Dim previousImage = _stripImage
        _stripImage = nextImage
        _trackBounds = trackBounds
        _selectedBounds = selectedBounds
        'Match the whole strip's pixel size so LakeUI's center-crop image mode
        'keeps the capsule at the intended size rather than stretching it.
        TabStripBackgroundImage = nextImage
        previousImage?.Dispose()
    End Sub

    Private Shared Function CreateCapsulePath(bounds As RectangleF) As GraphicsPath
        Dim path As New GraphicsPath()
        Dim diameter = Math.Min(bounds.Width, bounds.Height)
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 90, 180)
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 180)
        path.CloseFigure()
        Return path
    End Function

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then
            TabStripBackgroundImage = Nothing
            _stripImage?.Dispose()
            _stripImage = Nothing
        End If
        MyBase.Dispose(disposing)
    End Sub
End Class
