using Avalonia.Markup.Xaml;
using MaoX.Core;

namespace MaoX.Controls;

/// <summary>XAML 里的翻译：Text="{c:Tr 启动游戏}"，含英文逗号时加引号 {c:Tr '一，二'}。</summary>
public class TrExtension : MarkupExtension
{
    public TrExtension(string text)
    {
        Text = text;
    }

    public string Text { get; set; }

    /// <summary>同一句中文需要不同译法时的区分，例如 {c:Tr 账号, Context=settings}。</summary>
    public string Context { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        Context == null ? I18n.T(Text) : I18n.T(Text, Context);
}
