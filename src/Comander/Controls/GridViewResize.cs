using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Comander.Controls;

/// <summary>
/// Ограничение ресайза колонок GridView видимой областью ListView:
/// горизонтальный скролл в темах выключен, поэтому без ограничения колонку
/// можно утащить за край окна и контент теряется. Ползунок в шаблоне
/// GridViewColumnHeader должен называться НЕ PART_HeaderGripper (иначе встроенный
/// механизм WPF тоже обработает drag и получится двойное срабатывание) и иметь
/// GridViewResize.ClampDrag="True". На сам ListView вешается ClampToViewport="True" —
/// при уменьшении окна колонки пропорционально поджимаются до минимальной ширины.
/// </summary>
public static class GridViewResize
{
    const double MinColumnWidth = 30;

    /// <summary>На ползунке в шаблоне заголовка: ограничивает drag шириной области ListView.</summary>
    public static readonly DependencyProperty ClampDragProperty =
        DependencyProperty.RegisterAttached("ClampDrag", typeof(bool), typeof(GridViewResize),
            new PropertyMetadata(false, OnClampDragChanged));

    public static bool GetClampDrag(DependencyObject d) => (bool)d.GetValue(ClampDragProperty);
    public static void SetClampDrag(DependencyObject d, bool value) => d.SetValue(ClampDragProperty, value);

    /// <summary>На ListView: поджимает суммарную ширину колонок при уменьшении окна.</summary>
    public static readonly DependencyProperty ClampToViewportProperty =
        DependencyProperty.RegisterAttached("ClampToViewport", typeof(bool), typeof(GridViewResize),
            new PropertyMetadata(false, OnClampToViewportChanged));

    public static bool GetClampToViewport(DependencyObject d) => (bool)d.GetValue(ClampToViewportProperty);
    public static void SetClampToViewport(DependencyObject d, bool value) => d.SetValue(ClampToViewportProperty, value);

    static void OnClampDragChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Thumb thumb) return;
        thumb.Loaded += (_, _) =>
        {
            if (GetClampDrag(thumb))
                thumb.DragDelta += OnGripperDragDelta;
        };
    }

    static void OnGripperDragDelta(object sender, DragDeltaEventArgs e)
    {
        var thumb = (Thumb)sender;
        var header = FindAncestor<GridViewColumnHeader>(thumb);
        var list = FindAncestor<ListView>(thumb);
        var view = list?.View as GridView;
        if (header?.Column == null || view == null || list == null) return;

        double available = AvailableWidth(list, view);
        double others = view.Columns.Where(c => !ReferenceEquals(c, header.Column))
                                    .Sum(c => ActualColumnWidth(c));
        double max = Math.Max(MinColumnWidth, available - others);

        var col = header.Column;
        double proposed = (double.IsNaN(col.Width) ? header.ActualWidth : col.Width) + e.HorizontalChange;
        col.Width = Math.Min(Math.Max(proposed, MinColumnWidth), max);
    }

    static void OnClampToViewportChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListView list) return;
        list.Loaded += (_, _) =>
        {
            if (!GetClampToViewport(list)) return;
            list.SizeChanged += (_, _) => ClampColumns(list);
        };
    }

    /// <summary>
    /// Перечитывание кистей заголовков после смены темы. DynamicResource в сеттерах
    /// стиля GridViewHeaderStyle у GridViewColumnHeader НЕ инвалидируется при замене
    /// словаря темы в MergedDictionaries (стили остаются с цветами прежней темы) —
    /// лечится принудительным переприменением стиля к живым контейнерам заголовков.
    /// </summary>
    public static void RefreshHeaderChrome(ListView list)
    {
        list.Dispatcher.BeginInvoke(new Action(() =>
        {
            var style = (Style)list.FindResource("GridViewHeaderStyle");
            foreach (var h in Visuals<GridViewColumnHeader>(list))
            {
                var s = h.Style ?? style;
                h.Style = null;
                h.Style = s;
            }
        }), System.Windows.Threading.DispatcherPriority.Render);
    }

    static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T hit) yield return hit;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var t in Visuals<T>(VisualTreeHelper.GetChild(root, i)))
                yield return t;
    }

    /// <summary>Первый визуальный потомок заданного типа.</summary>
    public static IEnumerable<T> FindVisuals<T>(DependencyObject root) where T : DependencyObject => Visuals<T>(root);

    /// <summary>Если сумма колонок не влезает — поджимаем справа налево до минимума.</summary>
    static void ClampColumns(ListView list)
    {
        if (list.View is not GridView view) return;
        double available = AvailableWidth(list, view);
        var cols = view.Columns;
        double total = cols.Sum(ActualColumnWidth);
        if (total <= available) return;
        for (int i = cols.Count - 1; i >= 0 && total > available; i--)
        {
            double w = ActualColumnWidth(cols[i]);
            double target = Math.Max(MinColumnWidth, w - (total - available));
            total -= w - target;
            cols[i].Width = target;
        }
    }

    static double AvailableWidth(ListView list, GridView view)
    {
        // минус вертикальный скролл (если появится) и небольшой запас на рамку
        double vScroll = SystemParameters.VerticalScrollBarWidth + 6;
        return Math.Max(MinColumnWidth * 2, list.ActualWidth - vScroll);
    }

    static double ActualColumnWidth(GridViewColumn c) => double.IsNaN(c.Width) ? c.ActualWidth : c.Width;

    static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (; d != null; d = VisualTreeHelper.GetParent(d) ?? (LogicalTreeHelper.GetParent(d) as DependencyObject)!)
            if (d is T t) return t;
        return null;
    }
}
