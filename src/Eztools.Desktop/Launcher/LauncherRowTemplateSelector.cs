// Copyright (c) 2026 Eztools contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Eztools.Host.Launcher;

namespace Eztools.Desktop;

/// <summary>
/// 结果行的模板分流（W7-b，设计方案 §10.9 A）：**文件走既有 <see cref="SearchWindow.HitText"/>，
/// 其余来源走 <see cref="LauncherRowText"/>**。
///
/// <para><b>这不是"两种样式"，而是"零行为"的实现手段</b>：FR-10 要求文件项渲染与 W7 之前逐字节一致，
/// 因此 <c>HitText</c> 一行不改、探针的 <c>FindHitText</c>/<c>firstSegments</c> 断言零迁移。
/// 若把两者合并成一个模板，那条机器证据就没了（只有"看起来差不多"）。</para>
///
/// <para><b>为什么用 <see cref="DataTemplateSelector"/> 而不是 <c>DataTrigger</c></b>：分流判据是
/// "Kind 是否为 File 且 FileHit 非空"，属**结构性**条件；写成 XAML 触发器要在模板里塞条件分支，
/// 而 FEF 构造的数据模板不方便做多分支（这是 W3-d-2 用 XAML 常量 + XamlReader 的历史原因之一）。</para>
/// </summary>
internal sealed class LauncherRowTemplateSelector : DataTemplateSelector
{
    /// <summary>进程内单例（无状态，模板构造一次即可）。</summary>
    internal static readonly LauncherRowTemplateSelector Instance = new();

    private readonly DataTemplate _fileRow;
    private readonly DataTemplate _otherRow;

    private LauncherRowTemplateSelector()
    {
        // 文件行：绑定到 LauncherItem.FileHit（★ 唯一的改动点 —— HitText 自身零改动）
        var fileFactory = new FrameworkElementFactory(typeof(SearchWindow.HitText));
        fileFactory.SetBinding(SearchWindow.HitText.HitProperty, new Binding(nameof(LauncherItem.FileHit)));
        _fileRow = new DataTemplate { VisualTree = fileFactory };

        // 其余来源：绑定数据项自身
        var otherFactory = new FrameworkElementFactory(typeof(LauncherRowText));
        otherFactory.SetBinding(LauncherRowText.ItemProperty, new Binding());
        _otherRow = new DataTemplate { VisualTree = otherFactory };
    }

    public override DataTemplate SelectTemplate(object item, DependencyObject container) =>
        item is LauncherItem { Kind: LauncherKind.File, FileHit: not null } ? _fileRow : _otherRow;
}
