using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;

namespace AniMeido.Plugin.Base.Views.Controls;

public sealed partial class AnimeCard : UserControl
{
    public AnimeCard()
    {
        InitializeComponent();

        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        var visual = ElementCompositionPreview.GetElementVisual(this);
        var compositor = visual.Compositor;

        visual.CenterPoint = new System.Numerics.Vector3(
            (float)ActualWidth / 2,
            (float)ActualHeight / 2,
            0);

        visual.Properties.InsertVector3("Translation", new System.Numerics.Vector3(0, 0, 16));

        var scaleX = compositor.CreateScalarKeyFrameAnimation();
        scaleX.InsertKeyFrame(1.0f, 1.05f);
        scaleX.Duration = TimeSpan.FromMilliseconds(200);

        var scaleY = compositor.CreateScalarKeyFrameAnimation();
        scaleY.InsertKeyFrame(1.0f, 1.05f);
        scaleY.Duration = TimeSpan.FromMilliseconds(200);

        visual.StartAnimation("Scale.X", scaleX);
        visual.StartAnimation("Scale.Y", scaleY);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        var visual = ElementCompositionPreview.GetElementVisual(this);
        var compositor = visual.Compositor;

        visual.CenterPoint = new System.Numerics.Vector3(
            (float)ActualWidth / 2,
            (float)ActualHeight / 2,
            0);

        visual.Properties.InsertVector3("Translation", new System.Numerics.Vector3(0, 0, 0));

        var scaleX = compositor.CreateScalarKeyFrameAnimation();
        scaleX.InsertKeyFrame(1.0f, 1.0f);
        scaleX.Duration = TimeSpan.FromMilliseconds(200);

        var scaleY = compositor.CreateScalarKeyFrameAnimation();
        scaleY.InsertKeyFrame(1.0f, 1.0f);
        scaleY.Duration = TimeSpan.FromMilliseconds(200);

        visual.StartAnimation("Scale.X", scaleX);
        visual.StartAnimation("Scale.Y", scaleY);
    }
}
