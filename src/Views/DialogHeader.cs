using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace SourceGit.Views
{
    internal static class DialogHeader
    {
        internal static Grid Create(ChromelessWindow owner, TextBlock title = null, Button closeAction = null)
        {
            var header = new Grid { Name = "DialogTitleBar", Height = 28, IsVisible = !owner.UseSystemWindowFrame };
            var background = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
            background.Bind(Border.BackgroundProperty, new DynamicResourceExtension("Brush.TitleBar"));
            background.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("Brush.Border0"));
            background.PointerPressed += owner.BeginMoveWindow;
            header.Children.Add(background);
            var icon = new Avalonia.Controls.Shapes.Path { Width = 14, Height = 14, Margin = new Thickness(10, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                Data = owner.FindResource("Icons.Settings") as Geometry, IsHitTestVisible = false };
            icon.Bind(Avalonia.Controls.Shapes.Path.FillProperty, new DynamicResourceExtension("Brush.FG1"));
            header.Children.Add(icon);
            title ??= new TextBlock();
            title.Name = "DialogTitleText";
            title.ClearValue(TextBlock.FontSizeProperty);
            title.Bind(TextBlock.TextProperty, owner.GetObservable(Window.TitleProperty));
            title.FontWeight = FontWeight.Bold;
            title.Margin = new Thickness(36, 0);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.HorizontalAlignment = HorizontalAlignment.Center;
            title.VerticalAlignment = VerticalAlignment.Center;
            title.IsHitTestVisible = false;
            header.Children.Add(title);
            var caption = new CaptionButtons { IsCloseButtonOnly = true, HorizontalAlignment = HorizontalAlignment.Right };
            if (closeAction != null) caption.Bind(Control.IsEnabledProperty, closeAction.GetObservable(Control.IsEnabledProperty));
            header.Children.Add(caption);
            return header;
        }
    }
}
