using System.Windows;
using System.Windows.Controls;
using TjClient.Core;

namespace TjClient.Wpf;

static class QueuePresentationTests
{
    public static void AlignedCells() => CompactWindowTests.InSta(() =>
    {
        using var fixture = new Fixture();
        var window = new MainWindow(fixture.Root, false) { ShowActivated = false };
        try { window.Show(); window.AssertAlignedQueueCells(); }
        finally { window.DisposeTestWindow(); }
        return Task.CompletedTask;
    });
    public static void ReadableProgress() => CompactWindowTests.InSta(() =>
    {
        using var fixture = new Fixture();
        var window = new MainWindow(fixture.Root, false) { ShowActivated = false };
        try { window.Show(); window.AssertReadableProgress(); }
        finally { window.DisposeTestWindow(); }
        return Task.CompletedTask;
    });
}

public partial class MainWindow
{
    internal void AssertAlignedQueueCells()
    {
        ShowQueue([new QueueItem(new LocalFile("rphost_1/26112501.log", 2147483648, 0, 0), Offset: 1073741824)]);
        foreach (var width in new[] { 960d, MinWidth })
        {
            Width = width;
            UpdateLayout();
            var row = (QueueRow)QueueGrid.Items[0];
            var headers = QueueVisualChildren<System.Windows.Controls.Primitives.DataGridColumnHeader>(QueueGrid).Where(x => x.Column != null).ToList();
            foreach (var column in QueueGrid.Columns)
            {
                var text = (TextBlock)column.GetCellContent(row);
                var header = headers.Single(x => x.Column == column);
                var headerText = QueueVisualChildren<TextBlock>(header).Single(x => x.Text == column.Header.ToString());
                var textX = text.TransformToAncestor(QueueGrid).Transform(new Point()).X;
                var headerX = headerText.TransformToAncestor(QueueGrid).Transform(new Point()).X;
                Assert.True(Math.Abs(textX - headerX) <= 1, "Текст строки не выровнен с заголовком: " + column.Header);
                Assert.Equal(8d, text.Margin.Left);
                Assert.Equal(8d, text.Margin.Right);
            }
        }
    }
    private static IEnumerable<T> QueueVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T value) yield return value;
            foreach (var descendant in QueueVisualChildren<T>(child)) yield return descendant;
        }
    }
    internal void AssertReadableProgress()
    {
        var examples = new (long Offset, long Size, string Text)[]
        {
            (0, 0, "0 / 0 Б"),
            (42, 63, "42 / 63 Б"),
            (4507, 4507, "4,4 / 4,4 КиБ"),
            (1572864, 5242880, "1,5 / 5 МиБ"),
            (1073741824, 2147483648, "1 / 2 ГиБ"),
            (2147483648, 2147483648, "2 / 2 ГиБ"),
            (1, 2147483648, "<0,1 / 2 ГиБ"),
            (1073636966, 1073636966, "1023,9 / 1023,9 МиБ")
        };
        foreach (var (offset, size, expected) in examples)
        {
            ShowQueue([new QueueItem(new LocalFile("rphost_1/26112501.log", size, 0, 0), Offset: offset, JobId: 22, Error: "Синтетическая ошибка")]);
            var row = (QueueRow)QueueGrid.Items[0];
            Assert.Equal(expected, row.Progress);
            Assert.True(row.Details.Contains($"{offset:N0} / {size:N0} Б"), "Подсказка сохраняет точные байты");
            Assert.True(row.Details.Contains("Задание: 22") && row.Details.Contains("Синтетическая ошибка"));
            UpdateLayout();
            var text = (TextBlock)QueueGrid.Columns[2].GetCellContent(row);
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(text.DesiredSize.Width <= QueueGrid.Columns[2].ActualWidth - 2, "Читаемый прогресс должен помещаться в колонку с отступами");
        }
    }
}
