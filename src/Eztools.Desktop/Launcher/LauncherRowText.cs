// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Eztools.Host.Launcher;

namespace Eztools.Desktop;

/// <summary>
/// **非文件**结果行的渲染控件（W7-b，设计方案 §4.3 视觉契约 / §10.9 A）。
///
/// <para><b>为什么文件项不走这里</b>：文件的渲染必须与 W7 之前**逐字节一致**（FR-10），
/// 所以它继续走既有的 <c>SearchWindow.HitText</c>（代码与模板零改动）。本控件只服务
/// App / Calc / Unit / Encode 四种来源 —— 它们需要"徽标 + 图标 + 主副行"这套新结构。</para>
///
/// <para><b>结构契约（探针按此读数）</b>：<c>Children[0]=徽标</c> · <c>Children[1]=图标</c> ·
/// <c>Children[2]=纵向(主行, 副行)</c>。主行的高亮区间**直接按 provider 给的区间切**
/// （apps 本地匹配 / calc 结果行），本控件不重算匹配位置（与文件段同一条纪律）。</para>
///
/// <para><b>虚拟化复用安全</b>：<c>VirtualizationMode.Recycling</c> 会复用容器 —— 所以每次
/// 变更都**原地重置**四个子控件的状态（而不是往 Children 里追加），结构恒定。
/// （对照：<c>HitText</c> 是重建 Children，两者都安全，各有取舍。）</para>
/// </summary>
internal sealed class LauncherRowText : StackPanel
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(LauncherItem), typeof(LauncherRowText),
        new PropertyMetadata(null, OnItemChanged));

    private readonly TextBlock _badge;
    private readonly Image _icon;
    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;

    public LauncherItem? Item
    {
        get => (LauncherItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    /// <summary>探针用：本条渲染的徽标文本。</summary>
    internal string RenderedBadge => _badge.Text;

    /// <summary>
    /// 探针用：本条渲染的主行文本。
    /// ★ 必须从 **Inlines 聚合**读，不能读 <c>TextBlock.Text</c> —— 后者只反映"被显式赋值的 Text"，
    /// 而我们是用 Inlines 分段搭出来的，实测 <c>Text</c> 恒为空串（探针首跑抓到的假读数）。
    /// </summary>
    internal string RenderedTitle =>
        string.Concat(_title.Inlines.OfType<Run>().Select(r => r.Text));

    /// <summary>探针用：本条渲染的副行文本。</summary>
    internal string RenderedSubtitle => _subtitle.Text;

    /// <summary>探针用：是否有图标（未取到图标 ⇒ false，行内不显示图标但不影响其余内容）。</summary>
    internal bool RenderedIconShown => _icon.Source is not null;

    /// <summary>
    /// 探针用：**实际渲染出来的**主行高亮分段（文本 + 是否加粗）。
    /// 与 <c>HitText.RenderedSegments</c> 同款：读的是渲染结果，不是重算一遍（重算等于自证）。
    /// </summary>
    internal IReadOnlyList<(string Text, bool Bold)> RenderedSegments()
    {
        var list = new List<(string, bool)>(_title.Inlines.Count);
        foreach (var inline in _title.Inlines)
        {
            if (inline is Run run)
            {
                list.Add((run.Text, run.FontWeight == FontWeights.Bold));
            }
        }

        return list;
    }

    /// <summary>FEF 反射构造要求**公开无参构造器**（同 <c>HitText</c> 的教训）。</summary>
    public LauncherRowText()
    {
        Orientation = Orientation.Horizontal;
        Margin = new Thickness(0, 2, 0, 2);

        _badge = new TextBlock
        {
            Width = 18,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Foreground = Brushes.Gray,
        };

        _icon = new Image
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _title = new TextBlock { FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
        _subtitle = new TextBlock
        {
            FontSize = 11,
            Foreground = Brushes.Gray,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var text = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_title);
        text.Children.Add(_subtitle);

        Children.Add(_badge);
        Children.Add(_icon);
        Children.Add(text);
    }

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LauncherRowText row)
        {
            row.Render(e.NewValue as LauncherItem);
        }
    }

    private void Render(LauncherItem? item)
    {
        // 复用容器时要**清干净**（否则上一行的文字会残留 —— 与 HitText 的"第 3 条起显示错乱"同坑）
        _badge.Text = "";
        _title.Inlines.Clear();
        _subtitle.Text = "";
        _icon.Source = null;

        if (item is null)
        {
            return;
        }

        _badge.Text = BadgeOf(item.Kind);

        // 主行：按 provider 给的高亮区间分段（区间由 provider 负责，UI 不重算）
        var pos = 0;
        foreach (var (start, len) in item.Highlights.OrderBy(s => s.Start))
        {
            if (start > pos && start <= item.Title.Length)
            {
                _title.Inlines.Add(new Run(item.Title[pos..start]));
                pos = start;
            }

            // 边界钳制：渲染层不信任跨层数据（防御深界，同 HitText）
            var take = Math.Min(len, Math.Max(0, item.Title.Length - start));
            if (take > 0)
            {
                _title.Inlines.Add(new Run(item.Title.Substring(start, take))
                {
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.DodgerBlue,
                });
                pos = start + take;
            }
        }

        if (pos < item.Title.Length)
        {
            _title.Inlines.Add(new Run(item.Title[pos..]));
        }

        _subtitle.Text = item.Subtitle;
        _subtitle.Visibility = item.Subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 图标：只有 IconHint 非空才取（文件/计算/换算/编码都没有图标）
        if (item.IconHint.Length > 0)
        {
            _icon.Source = IconCache.GetIcon(item.IconHint);
        }

        _icon.Visibility = _icon.Source is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>类型徽标（设计方案 §4.3）。用**单字符纯文本**：探针能整段读回断言，不需要截图比对。</summary>
    private static string BadgeOf(LauncherKind kind) => kind switch
    {
        LauncherKind.App => "▸",
        LauncherKind.Calc => "=",
        LauncherKind.Unit => "⇄",
        LauncherKind.Encode => "{}",
        LauncherKind.Clip => "⧉",
        LauncherKind.Command => "⌘",
        _ => "",   // File 不走本控件（防御：真走到这里也不加徽标 —— FR-10 的口径）
    };
}
